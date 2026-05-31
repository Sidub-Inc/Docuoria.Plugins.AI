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
// Every script `#load`s this file. It deduplicates DI wiring, env-var handling, arg parsing,
// and JSON I/O so individual scripts can focus on their semantics.
//
// The `dotnet-script` runtime is required to execute these scripts:
//   dotnet tool install -g dotnet-script

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Docuoria.Contracts;
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
    /// dry-run) to avoid forcing env-var configuration on irrelevant invocations.</param>
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
                    RegisterStore(b);
            });
        });
        return builder.Build();
    }

    public static IDocuoriaEngine GetEngine(IHost host)
        => host.Services.GetRequiredService<IDocuoriaEngine>();

    public static ITemplateStoreProvider? GetStore(IHost host)
        => host.Services.GetService<ITemplateStoreProvider>();

    private static void RegisterStore(IDocuoriaEngineBuilder builder)
    {
        var kind = Environment.GetEnvironmentVariable("DOCUORIA_STORE");
        if (string.IsNullOrWhiteSpace(kind))
            kind = "local";

        switch (kind.Trim().ToLowerInvariant())
        {
            case "local":
            {
                var path = Environment.GetEnvironmentVariable("DOCUORIA_STORE_LOCAL_PATH");
                if (string.IsNullOrWhiteSpace(path))
                    path = "./templates";
                // IN-01: directory is created lazily by LocalFileTemplateStoreProvider on first
                // write; ListAsync tolerates a missing root. Avoid littering arbitrary cwds with
                // an empty ./templates dir at script startup.
                builder.AddLocalTemplateStore(path!);
                break;
            }
            case "api":
            {
                var url = Environment.GetEnvironmentVariable("DOCUORIA_STORE_API_URL");
                if (string.IsNullOrWhiteSpace(url))
                    throw new InvalidOperationException(
                        "DOCUORIA_STORE_API_URL is required when DOCUORIA_STORE='api'.");
                var key = Environment.GetEnvironmentVariable("DOCUORIA_STORE_API_KEY");
                var creds = new ApiTemplateStoreCredentials { FunctionKey = key };
                builder.AddApiTemplateStore(new Uri(url!), creds);
                break;
            }
            default:
                throw new InvalidOperationException(
                    $"Unknown DOCUORIA_STORE value '{kind}'. Expected 'local' or 'api'.");
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
