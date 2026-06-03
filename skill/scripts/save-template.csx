#load "_common.csx"

#nullable enable

// SCR-10 — persist a template JSON file to the configured store.
// Args: --template <path> [--overwrite] [--store-path <dir>] [--store-url <url>] [--store-key <key>]
// stdout: { status, identifier }.

using Docuoria.Models;
using Docuoria.Storage;
using Docuoria.Storage.Exceptions;

try
{
    Cli.Help(Args, "save-template.csx", "Persist a template JSON file to the configured store",
        ("template", true, "Path to the template JSON file", false),
        ("overwrite", false, "Overwrite if template already exists", true),
        ("store-path", false, "Local template store directory (default: ./templates)", false),
        ("store-url", false, "API template store URL (mutually exclusive with --store-path)", false),
        ("store-key", false, "Function key for API store authentication", false));

    var filePath = Cli.Require(Args, "template");
    var overwrite = Cli.Has(Args, "overwrite");

    if (!File.Exists(filePath))
    {
        JsonOut.Error("template-not-found", $"Template file not found at '{filePath}'", null, 1);
    }

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: true);
    var store = ScriptHost.GetStore(host);
    if (store is null)
    {
        JsonOut.Error("no-store", "ITemplateStoreProvider is not registered. Pass --store-path <dir> to specify a local template store directory.", null, 1);
    }

    var tpl = Template.FromJson(File.ReadAllText(filePath));

    try
    {
        await store!.SaveAsync(tpl, overwrite);
    }
    catch (TemplateAlreadyExistsException tae)
    {
        JsonOut.Error("already-exists", $"{tae.Message} Use --overwrite to replace the existing template.", null, 1);
    }

    JsonOut.Write(new { status = "ok", identifier = tpl.Identifier });
}
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
