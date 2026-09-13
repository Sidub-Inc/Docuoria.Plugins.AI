#r "nuget: Microsoft.Extensions.Hosting, 10.0.12"
#r "nuget: Microsoft.Extensions.DependencyInjection, 10.0.12"
#r "nuget: Microsoft.Extensions.Http, 10.0.12"
#r "nuget: PdfPig, 0.1.14"
#r "nuget: Tabula, 1.0.1"
#r "nuget: CsvHelper, 33.1.0"
#r "nuget: pythonnet, 3.0.5"
#r "nuget: Sidub.Licensing.Client, 2.0.10"
#r "../assets/lib/Docuoria.dll"

#nullable enable

// Phase 29: Shared bootstrap for every script under `scripts/` (D-Area-2 in 29-CONTEXT.md).
// Every script `#load`s this file. It deduplicates DI wiring, arg parsing,
// and JSON I/O so individual scripts can focus on their semantics.
//
// The `dotnet-script` runtime is required to execute these scripts:
//   dotnet tool install -g dotnet-script

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Docuoria.Contracts;
using Docuoria.Licensing;
using Docuoria.Output.Ledger;
using Docuoria.Registration;
using Docuoria.Serialization;
using Docuoria.Storage;

// Dev-time override: DOCUORIA_SDK_DLL can point at an alternate Docuoria.dll.
// The `#r` literal above resolves at script-compile time; this LoadFrom call after-the-fact
// ensures the override assembly is available for reflection-based DI lookups even when the
// literal succeeded.
if (Environment.GetEnvironmentVariable("DOCUORIA_SDK_DLL") is string __sdkPath
    && !string.IsNullOrWhiteSpace(__sdkPath)
    && File.Exists(__sdkPath))
{
    try
    {
        Assembly.LoadFrom(__sdkPath);
    }
    catch (Exception __sdkLoadEx)
    {
        // WR-02: DOCUORIA_SDK_DLL is an explicit opt-in dev override. If the user set it
        // but loading failed, do NOT silently fall back to the #r literal — emit a structured
        // JSON error envelope to stderr and exit non-zero so the override failure is visible.
        var payload = new { error = new { code = "sdk-load-failed", message = $"DOCUORIA_SDK_DLL load failed: {__sdkLoadEx.Message}", detail = __sdkPath } };
        var json = JsonSerializer.Serialize(payload, DocuoriaJsonOptions.Default);
        Console.Error.WriteLine(json);
        Environment.Exit(1);
    }
}

/// <summary>
/// Builds a Generic Host with the SDK engine + (optionally) the configured template store.
/// </summary>
public static class ScriptHost
{
    /// <summary>
    /// Construct an IHost containing IDocuoriaEngine and (optionally) ITemplateStoreProvider.
    /// </summary>
    /// <param name="args">Forwarded to Host.CreateDefaultBuilder for configuration binding.</param>
    /// <param name="includeStore">When false, ITemplateStoreProvider is NOT registered — used by
    /// scripts that don't touch the store (inspect, test-pattern, test-groups, validate-template,
    /// dry-run) to avoid forcing store configuration on irrelevant invocations.</param>
    public static IHost CreateHost(string[] args, bool includeStore = true)
    {
        var builder = Host.CreateDefaultBuilder(args);

        // Both script streams are contracts: stdout is a SINGLE line of result JSON, stderr a
        // SINGLE line of error JSON, and callers parse both. The generic host's default console
        // logger writes to stdout, and the licensing client logs at Information as it initialises,
        // so a licensed run would interleave log lines with the result and break every consumer.
        // Routing those logs to stderr only moves the breakage onto the error contract, so by
        // default the scripts carry no log providers at all -- diagnostics travel in the error
        // envelope's `detail` field instead.
        //
        // DOCUORIA_LOG_LEVEL is the debugging escape hatch: set it (Trace/Debug/Information/...)
        // to get logs on stderr, accepting that the stderr JSON contract no longer holds.
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();

            var configured = Environment.GetEnvironmentVariable("DOCUORIA_LOG_LEVEL");
            if (!string.IsNullOrWhiteSpace(configured)
                && Enum.TryParse<LogLevel>(configured, ignoreCase: true, out var level))
            {
                logging.SetMinimumLevel(level);
                logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            }
        });

        builder.ConfigureServices(services =>
        {
            services.AddDocuoriaEngine(b =>
            {
                b.AddBuiltInMatchRules();
                b.AddCsvOutputGenerator();
                b.AddJsonOutputGenerator();
                if (includeStore)
                    RegisterStore(b, args);
            });
            // Licensing (distributed channel): enforce by default so the skill product gates
            // features and surfaces the acquisition journey. DOCUORIA_ENFORCEMENT can override
            // (e.g. Disabled for local development) and DOCUORIA_ENVIRONMENT selects a non-production
            // deployment — both without recompiling. Enforcement logic lives in the SDK guard.
            //
            // The license lives BESIDE the skill (`<scripts-dir>/docuoria.license.json`) so the skill
            // is self-contained and the CLI tool is not a dependency: `license-set.csx` writes this
            // file and the SDK reads it first. The SDK then falls back to the user-home file
            // (`~/.docuoria/license.json`), which is where `docuoria license acquire` stores a
            // licence, so either acquisition path lights up the skill.
            var skillLicensePath = ResolveSkillLicensePath();
            services.AddDocuoriaLicensingForDistribution(o =>
            {
                if (skillLicensePath is not null)
                    o.LicensePath = skillLicensePath;
            });
        });
        var host = builder.Build();
        _current = host;
        return host;
    }

    private static IHost? _current;

    /// <summary>
    /// Disposes the live host and terminates the process with <paramref name="exitCode"/>. Every
    /// non-returning exit in the scripts goes through here. <see cref="Environment.Exit(int)"/> on
    /// its own never unwinds the <c>using var host</c> in the calling script, and the licensing
    /// client delivers buffered consumption only when the host is disposed, so a bare exit silently
    /// dropped the metered usage of every failed or partial run.
    /// </summary>
    [DoesNotReturn]
    public static void Exit(int exitCode)
    {
        var host = _current;
        _current = null;
        if (host is not null)
        {
            try { host.Dispose(); }
            catch { /* cleanup must never mask the real exit code */ }
        }
        Environment.Exit(exitCode);
        throw new InvalidOperationException("Environment.Exit returned unexpectedly.");
    }

    /// <summary>
    /// Absolute directory of this script library (where <c>_common.csx</c> lives) — the skill's
    /// scripts directory at runtime. Captured at compile time via <c>[CallerFilePath]</c>; falls
    /// back to the current directory when unavailable.
    /// </summary>
    public static string ScriptDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => (!string.IsNullOrEmpty(path) && Path.GetDirectoryName(path) is { Length: > 0 } dir)
            ? dir
            : Directory.GetCurrentDirectory();

    /// <summary>
    /// Skill-local license file path (<c>&lt;scripts-dir&gt;/docuoria.license.json</c>): the file the
    /// SDK store writes and reads first, so the license travels with the skill install. Precedence:
    /// an explicit <c>DOCUORIA_LICENSE_PATH</c> always wins (a named file is the most specific
    /// instruction); otherwise a <c>DOCUORIA_HOME</c> directory convention returns
    /// <see langword="null"/> so the store uses <c>%DOCUORIA_HOME%/license.json</c> alone; otherwise
    /// the default beside the scripts. Whatever the primary file, the store also falls back to the
    /// user-home file on read. The <c>DOCUORIA_LICENSE</c> environment variable beats every file.
    /// </summary>
    public static string? ResolveSkillLicensePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("DOCUORIA_LICENSE_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCUORIA_HOME")))
            return null;

        return Path.Combine(ScriptDirectory(), DocuoriaLicenseOptions.LicenseFileName);
    }

    public static IDocuoriaEngine GetEngine(IHost host)
        => host.Services.GetRequiredService<IDocuoriaEngine>();

    public static ITemplateStoreProvider? GetStore(IHost host)
        => host.Services.GetService<ITemplateStoreProvider>();

    /// <summary>SDK license guard — used by the ledger-append metering call sites.</summary>
    public static IDocuoriaLicenseGuard GetLicenseGuard(IHost host)
        => host.Services.GetService<IDocuoriaLicenseGuard>() ?? NoOpLicenseGuard.Instance;

    /// <summary>License status surface (license-status.csx).</summary>
    public static IDocuoriaLicenseInfo GetLicenseInfo(IHost host)
        => host.Services.GetRequiredService<IDocuoriaLicenseInfo>();

    /// <summary>License key store (license-set.csx / license-remove.csx).</summary>
    public static LicenseKeyStore GetLicenseStore(IHost host)
        => host.Services.GetRequiredService<LicenseKeyStore>();

    /// <summary>Free-tier acquisition service (license-acquire.csx).</summary>
    public static IDocuoriaLicenseAcquisition GetLicenseAcquisition(IHost host)
        => host.Services.GetRequiredService<IDocuoriaLicenseAcquisition>();

    private static void RegisterStore(IDocuoriaEngineBuilder builder, string[] args)
    {
        var storePath = Cli.Get(args, "store-path");
        var storeUrl = Cli.Get(args, "store-url");
        var storeKey = Cli.Get(args, "store-key");

        if (!string.IsNullOrWhiteSpace(storePath) && !string.IsNullOrWhiteSpace(storeUrl))
        {
            throw new InvalidOperationException(
                "--store-path and --store-url are mutually exclusive. Use --store-path for a local file store or --store-url for an API store.");
        }

        if (!string.IsNullOrWhiteSpace(storeUrl))
        {
            var creds = new ApiTemplateStoreCredentials { FunctionKey = storeKey };
            builder.AddApiTemplateStore(new Uri(storeUrl), creds);
        }
        else
        {
            // IN-01: directory is created lazily by LocalFileTemplateStoreProvider on first
            // write; ListAsync tolerates a missing root. Avoid littering arbitrary cwds with
            // an empty ./templates dir at script startup.
            var path = string.IsNullOrWhiteSpace(storePath) ? "./templates" : storePath;
            builder.AddLocalTemplateStore(path);
        }
    }
}

/// <summary>Hand-rolled `--key value` / `--flag` arg parser (System.CommandLine deferred).</summary>
/// <remarks>Named <c>Cli</c> rather than <c>Args</c> because <c>dotnet-script</c> exposes a
/// top-level <c>Args</c> global (<see cref="System.Collections.Generic.IList{T}"/> of
/// <see cref="string"/>) that would shadow a static class of the same name.</remarks>
public static class Cli
{
    private static readonly List<(string Name, bool Required, string Description, bool IsFlag)> _registeredArgs = new();
    private static string? _scriptDescription;

    /// <summary>
    /// Register the script description and check for --help. Call at the top of each script.
    /// If --help is present, prints usage and exits.
    /// </summary>
    public static void Help(IList<string> args, string scriptName, string description, params (string Name, bool Required, string Description, bool IsFlag)[] argDefs)
    {
        _scriptDescription = description;
        _registeredArgs.Clear();
        _registeredArgs.AddRange(argDefs);

        if (Has(args, "help") || Has(args, "h"))
        {
            Console.WriteLine();
            Console.WriteLine($"  {scriptName}");
            Console.WriteLine($"  {description}");
            Console.WriteLine();
            Console.WriteLine("  Usage:");
            Console.WriteLine($"    dotnet script scripts/{scriptName} -- [args]");
            Console.WriteLine();
            Console.WriteLine("  Arguments:");
            foreach (var (name, required, desc, isFlag) in argDefs)
            {
                var req = required ? "(required)" : "(optional)";
                var kind = isFlag ? "flag" : "value";
                Console.WriteLine($"    --{name,-20} {req,-12} {desc}");
            }
            Console.WriteLine($"    --{"help",-20} {"(optional)",-12} Show this help message");
            Console.WriteLine();
            Environment.Exit(0);
        }

        Validate(args, argDefs);
    }

    /// <summary>
    /// Rejects any <c>--flag</c> the script did not declare, and any stray positional token, with
    /// <c>unknown-arg</c> (exit 2) and the list of valid flags. Agents learn flags only from the
    /// documentation; a mistyped or invented flag used to be ignored silently and surfaced later as
    /// a confusing <c>missing-arg</c> or as a run that quietly did something else.
    /// </summary>
    public static void Validate(IList<string> args, IReadOnlyList<(string Name, bool Required, string Description, bool IsFlag)> argDefs)
    {
        if (args is null || args.Count == 0) return;

        var valid = string.Join(", ", argDefs.Select(d => "--" + d.Name).Append("--help"));
        for (int i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                var name = token.Substring(2);
                if (name == "help" || name == "h") continue;

                var def = argDefs.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));
                if (def.Name is null)
                {
                    JsonOut.Error("unknown-arg",
                        $"Unknown argument '{token}'. Valid flags: {valid}.", null, 2);
                }
                if (!def.IsFlag) i++; // the next token is this flag's value
                continue;
            }

            JsonOut.Error("unknown-arg",
                $"Unexpected positional argument '{token}'. Every value must follow its flag. Valid flags: {valid}.", null, 2);
        }
    }

    /// <summary>Returns the value following <c>--key</c>, or null when absent.</summary>
    public static string? Get(string[] args, string key)
    {
        if (args is null) return null;
        var marker = "--" + key;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], marker, StringComparison.Ordinal))
            {
                if (i + 1 < args.Length) return args[i + 1];
                return null;
            }
        }
        return null;
    }

    /// <summary>True when <c>--key</c> is present (regardless of any following value).</summary>
    public static bool Has(string[] args, string key)
    {
        if (args is null) return false;
        var marker = "--" + key;
        foreach (var a in args)
        {
            if (string.Equals(a, marker, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Required-arg lookup. On missing key emits a JSON error to stderr and exits with code 2.
    /// </summary>
    public static string Require(IList<string> args, string key) => Require(args.ToArray(), key);
    public static string? Get(IList<string> args, string key) => Get(args.ToArray(), key);
    public static bool Has(IList<string> args, string key) => Has(args.ToArray(), key);

    public static string Require(string[] args, string key)
    {
        var v = Get(args, key);
        if (v is null)
        {
            JsonOut.Error("missing-arg", $"--{key} is required", null, 2);
        }
        return v!;
    }
}

/// <summary>JSON stdout / stderr writers using DocuoriaJsonOptions.Default (D-Area-1).</summary>
public static class JsonOut
{
    /// <summary>Serialize <paramref name="value"/> to a single stdout line.</summary>
    public static void Write(object value)
    {
        var json = JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), DocuoriaJsonOptions.Default);
        Console.Out.WriteLine(json);
    }

    /// <summary>Write a pre-serialized JSON string to stdout (avoids double-serialization).</summary>
    public static void WriteRaw(string json)
    {
        Console.Out.WriteLine(json);
    }

    /// <summary>
    /// Emit a structured error envelope to stderr and terminate the script with
    /// <paramref name="exitCode"/> (default 1). Errors NEVER go to stdout. This method
    /// does not return; <see cref="Environment.Exit(int)"/> terminates the process. The
    /// trailing <c>throw</c> is unreachable in practice but communicates the no-return
    /// contract to the C# flow analyzer so callers don't need to null-forgive the result.
    /// </summary>
    [DoesNotReturn]
    public static void Error(string code, string message, string? detail = null, int exitCode = 1)
    {
        var payload = new { error = new { code, message, detail } };
        var json = JsonSerializer.Serialize(payload, DocuoriaJsonOptions.Default);
        Console.Error.WriteLine(json);
        ScriptHost.Exit(exitCode);
    }

    /// <summary>True for any Docuoria licence failure (required, inactive, invalid, unavailable, offering, feature, rate limit).</summary>
    public static bool IsLicenseFailure(Exception ex) => ex is DocuoriaLicenseException;

    /// <summary>
    /// Terminal failure mapper used by every script's outer catch. Docuoria license failures carry
    /// deterministic message prefixes (DOCUORIA_LICENSE_REQUIRED:, DOCUORIA_LICENSE_INACTIVE:&lt;reason&gt;,
    /// DOCUORIA_LICENSE_INVALID:, DOCUORIA_LICENSE_UNAVAILABLE:, DOCUORIA_OFFERING_UNAVAILABLE:,
    /// DOCUORIA_FEATURE_DENIED:&lt;key&gt;, DOCUORIA_RATE_LIMIT:&lt;key&gt;) so an agent reading stderr knows
    /// exactly what to tell the user.
    /// </summary>
    /// <remarks>
    /// Exit codes carry one distinction: <b>3 means the license itself needs attention</b> — it is
    /// missing, expired/suspended, or no longer valid, and the user has to go do something about it
    /// before any further call can succeed. <b>1 means this call failed</b> but the license is fine:
    /// the feature is not in the plan, the usage window is exhausted, or the service is unreachable.
    /// An agent should route a 3 to the acquisition/renewal journey and treat a 1 as a per-call
    /// outcome. Exit <b>2</b> is the third: <c>offering-unavailable</c>, where the offering or
    /// seller named does not exist — a configuration mistake rather than anything to do with a
    /// licence. Every case is enumerated because the provider's exceptions have no common base;
    /// anything unmapped reaches the user as "unhandled".
    /// </remarks>
    [DoesNotReturn]
    public static void Fail(Exception ex, string? context = null)
    {
        // `context` lets a caller that stopped part-way (batch-execute) say what was completed and
        // what remains; it is appended to the standard remediation so the agent reads one detail.
        string With(string detail) => string.IsNullOrWhiteSpace(context) ? detail : detail + " " + context;

        switch (ex)
        {
            case DocuoriaLicenseRequiredException lre:
                Error("license-required", lre.Message, With(
                    "Get a free licence: run 'dotnet script scripts/license-acquire.csx' (it returns the marketplace " +
                    "link; store the issued key with 'dotnet script scripts/license-set.csx -- --key <key>'), or on a " +
                    "machine with the Docuoria CLI run 'docuoria license acquire' to sign in without pasting a key."), 3);
                break;
            case DocuoriaOfferingUnavailableException oue:
                // A configuration mistake, not a licence problem and not an outage: exit 2, the
                // same code every other "you asked for something that is not there" uses.
                Error("offering-unavailable", oue.Message, With(
                    "Check the offering and issuer ids match the deployment you are pointed at."), 2);
                break;
            case DocuoriaLicenseInactiveException lie:
                Error("license-inactive", lie.Message, With(
                    $"The license is present but not active ({lie.Reason}). Renew or resume it, then retry."), 3);
                break;
            case DocuoriaLicenseInvalidException live:
                Error("license-invalid", live.Message, With(
                    "The stored credential could not be validated. Remove it with " +
                    "'dotnet script scripts/license-remove.csx' and acquire a new one." +
                    (string.IsNullOrWhiteSpace(live.CorrelationId)
                        ? string.Empty
                        : $" Quote correlation id {live.CorrelationId} to support.")), 3);
                break;
            case DocuoriaLicenseUnavailableException lue:
                // Not the user's license, and not their fault -- retrying is the remediation, so
                // this stays exit 1 rather than routing them into the acquisition journey.
                Error("license-unavailable", lue.Message, With(
                    "The licensing service is unreachable and the offline grace period has elapsed. " +
                    "Reconnect and retry." +
                    (string.IsNullOrWhiteSpace(lue.CorrelationId)
                        ? string.Empty
                        : $" Quote correlation id {lue.CorrelationId} to support.")), 1);
                break;
            case DocuoriaFeatureDeniedException fde:
                Error("feature-denied", fde.Message, With(
                    $"The active license does not include '{fde.FeatureKey}'."), 1);
                break;
            case DocuoriaRateLimitException rle:
                Error("rate-limit", rle.Message, With(
                    $"The usage window for '{rle.FeatureKey}' is exhausted; it resets automatically within a minute."), 1);
                break;
            default:
                Error("unhandled", ex.Message, With(ex.ToString()), 1);
                break;
        }
    }

    /// <summary>
    /// Terminal mapper for the acquisition scripts. Lives here rather than in the calling script
    /// because only this file carries the SDK <c>#r</c>: in dotnet-script a reference from a
    /// <c>#load</c>ed file lets the loader USE those types but not NAME them, so a script that
    /// writes <c>catch (DocuoriaCheckoutUnavailableException)</c> fails to compile. Keeping the
    /// type names on this side of the boundary is what makes the calling script buildable.
    /// </summary>
    [DoesNotReturn]
    public static void FailAcquisition(Exception ex)
    {
        switch (ex)
        {
            case DocuoriaCheckoutUnavailableException cue:
                // The scripts carry no buyer sign-in (device code needs a person at a browser, and
                // this channel is agent-driven), so free acquisition from here is always the
                // marketplace-paste path. Hand the agent the purchase URL as data so it can give
                // the user a real link rather than restating a bare domain.
                Error("checkout-unavailable", cue.Message,
                    $"Sign-in is not available from the scripts. Ask the user to open {cue.PurchaseUrl}, get a free key for "
                    + $"\"{DocuoriaLicenseOptions.MarketplaceProductName}\", and paste it back; then store it with "
                    + "'dotnet script scripts/license-set.csx -- --key <key>' and retry the original command. On a machine "
                    + "with the Docuoria CLI, 'docuoria license acquire' signs in directly and the skill picks the licence up.", 1);
                break;
            case InvalidOperationException ioe:
                Error("not-provisioned", ioe.Message,
                    "Obtain a key from the marketplace and store it with license-set.csx.", 1);
                break;
            default:
                Fail(ex);
                break;
        }

        throw new InvalidOperationException("Error returned unexpectedly.");
    }
}

/// <summary>
/// Opens a readable+seekable FileStream over <paramref name="path"/>, or emits a JSON error and
/// exits when the file is missing.
/// </summary>
public static FileStream LoadPdf(string path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
        JsonOut.Error("pdf-not-found", $"PDF not found at '{path}'", null, 1);
    }
    return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
}

/// <summary>
/// File I/O and arg plumbing for export ledgers (recurring-export-ledgers). The merge
/// semantics live in the SDK (`Docuoria.Output.Ledger`); this class owns the parts the
/// SDK deliberately does not: reading existing files, atomic writes, and CLI policy args.
/// </summary>
public static class LedgerIo
{
    /// <summary>Ledger files are UTF-8 without BOM, matching <c>CsvGeneratorOptions</c> defaults.</summary>
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Parses <c>--on-duplicate</c> (skip | replace | fail; default skip). Emits
    /// <c>bad-on-duplicate</c> and exits 2 on any other value.
    /// </summary>
    public static DuplicateSourcePolicy ParseDuplicatePolicy(IList<string> args)
    {
        var raw = Cli.Get(args, "on-duplicate");
        if (raw is null) return DuplicateSourcePolicy.Skip;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "skip": return DuplicateSourcePolicy.Skip;
            case "replace": return DuplicateSourcePolicy.Replace;
            case "fail": return DuplicateSourcePolicy.Fail;
            default:
                JsonOut.Error("bad-on-duplicate",
                    $"--on-duplicate must be skip, replace, or fail (got '{raw}')", null, 2);
                return default; // unreachable
        }
    }

    /// <summary>
    /// Reads and parses an existing CSV ledger at <paramref name="path"/>; a missing file is an
    /// empty ledger (create-or-extend). Unrecognized content emits <c>not-a-ledger</c> and exits 1
    /// without touching the file.
    /// </summary>
    public static CsvLedger ReadCsvLedger(string path)
    {
        if (!File.Exists(path)) return CsvLedger.CreateEmpty();
        var content = File.ReadAllText(path);
        if (!CsvLedger.TryParse(content, out var ledger, out var error))
        {
            JsonOut.Error("not-a-ledger",
                $"'{path}' exists but is not a recognizable CSV ledger - refusing to touch it.",
                $"{error}. If it is a JSON ledger, pass --format json; otherwise point --output at a new path.", 1);
        }
        return ledger!;
    }

    /// <summary>JSON counterpart of <see cref="ReadCsvLedger"/>.</summary>
    public static JsonLedger ReadJsonLedger(string path)
    {
        if (!File.Exists(path)) return JsonLedger.CreateEmpty();
        var content = File.ReadAllText(path);
        if (!JsonLedger.TryParse(content, out var ledger, out var error))
        {
            JsonOut.Error("not-a-ledger",
                $"'{path}' exists but is not a recognizable JSON ledger - refusing to touch it.",
                $"{error}. If it is a CSV ledger, pass --format csv; otherwise point --output at a new path.", 1);
        }
        return ledger!;
    }

    /// <summary>
    /// True when the file exists with non-whitespace content recognized as a Docuoria ledger of
    /// either format. Used by non-append writes to refuse flattening an accumulating file.
    /// </summary>
    public static bool IsRecognizedLedger(string path, out IReadOnlyList<string> recordedSources)
    {
        recordedSources = Array.Empty<string>();
        if (!File.Exists(path)) return false;
        string content;
        try { content = File.ReadAllText(path); }
        catch { return false; }
        if (string.IsNullOrWhiteSpace(content)) return false;

        if (CsvLedger.TryParse(content, out var csv, out _))
        {
            recordedSources = csv!.SourceFiles;
            return true;
        }
        if (JsonLedger.TryParse(content, out var json, out _))
        {
            recordedSources = json!.SourceFiles;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Writes ledger text atomically: temp file in the target directory, then replace. A crash
    /// mid-write leaves the original file intact.
    /// </summary>
    public static void WriteAtomic(string path, string content)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir!,
            $".{Path.GetFileName(full)}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tmp, content, Utf8NoBom);
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Maps a merge action to the envelope vocabulary (appended / replaced / skipped-duplicate).</summary>
    public static string ActionLabel(LedgerMergeAction action) => action switch
    {
        LedgerMergeAction.Appended => "appended",
        LedgerMergeAction.Replaced => "replaced",
        _ => "skipped-duplicate",
    };

}
