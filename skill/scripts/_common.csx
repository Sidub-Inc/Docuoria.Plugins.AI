#r "nuget: Microsoft.Extensions.Hosting, 10.0.0"
#r "nuget: Microsoft.Extensions.DependencyInjection, 10.0.0"
#r "nuget: Microsoft.Extensions.Http, 10.0.0"
#r "nuget: PdfPig, 0.1.14"
#r "nuget: Tabula, 1.0.1"
#r "nuget: CsvHelper, 33.1.0"
#r "nuget: pythonnet, 3.0.5"
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
using Docuoria.Contracts;
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
        });
        return builder.Build();
    }

    public static IDocuoriaEngine GetEngine(IHost host)
        => host.Services.GetRequiredService<IDocuoriaEngine>();

    public static ITemplateStoreProvider? GetStore(IHost host)
        => host.Services.GetService<ITemplateStoreProvider>();

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
        Environment.Exit(exitCode);
        throw new InvalidOperationException("Environment.Exit returned unexpectedly.");
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
