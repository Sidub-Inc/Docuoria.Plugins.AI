#load "_common.csx"

#nullable enable

// SCR-09 — fetch a template by ID and emit its JSON.
// Args: --id <identifier> [--output <path>]
// stdout: parsed template JSON or { status, path }.

using System.Text.Json;
using Docuoria.Serialization;
using Docuoria.Storage;

try
{
    Cli.Help(Args, "load-template.csx", "Fetch a template by ID and emit its JSON",
        ("id", true, "Template identifier", false),
        ("output", false, "Write template JSON to this file path", false),
        ("store-path", false, "Local template store directory (default: ./templates)", false),
        ("store-url", false, "API template store URL (mutually exclusive with --store-path)", false),
        ("store-key", false, "Function key for API store authentication", false));

    var id = Cli.Require(Args, "id");
    var outputPath = Cli.Get(Args, "output");

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: true);
    var store = ScriptHost.GetStore(host);
    if (store is null)
    {
        JsonOut.Error("no-store", "ITemplateStoreProvider is not registered. Pass --store-path <dir> to specify a local template store directory.", null, 1);
    }

    var tpl = await store!.LoadAsync(id);
    if (tpl is null)
    {
        JsonOut.Error("not-found", $"Template '{id}' not found. Run list-templates.csx to see available template IDs.", null, 1);
    }

    var json = tpl!.ToJson();
    if (!string.IsNullOrEmpty(outputPath))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath!))!);
        await File.WriteAllTextAsync(outputPath!, json);
        JsonOut.Write(new { status = "ok", path = outputPath });
    }
    else
    {
        var element = JsonSerializer.Deserialize<JsonElement>(json, DocuoriaJsonOptions.Default);
        JsonOut.Write(element);
    }
}
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
