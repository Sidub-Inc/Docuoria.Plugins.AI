#load "_common.csx"

#nullable enable

// SCR-10 — persist a template JSON file to the configured store.
// Args: --template <path> [--overwrite]  (also accepts --file as alias)
// stdout: { status, identifier }.

using Docuoria.Models;
using Docuoria.Storage;
using Docuoria.Storage.Exceptions;

try
{
    Cli.Help(Args, "save-template.csx", "Persist a template JSON file to the configured store",
        ("template", true, "Path to the template JSON file", false),
        ("overwrite", false, "Overwrite if template already exists", true));

    var filePath = Cli.Get(Args, "template") ?? Cli.Get(Args, "file");
    if (filePath is null)
    {
        JsonOut.Error("missing-arg", "--template is required (path to the template JSON file)", null, 2);
    }
    var overwrite = Cli.Has(Args, "overwrite");

    if (!File.Exists(filePath))
    {
        JsonOut.Error("template-not-found", $"Template file not found at '{filePath}'", null, 1);
    }

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: true);
    var store = ScriptHost.GetStore(host);
    if (store is null)
    {
        JsonOut.Error("no-store", "ITemplateStoreProvider is not registered. Set the DOCUORIA_STORE_LOCAL_PATH environment variable to a directory path to enable the local template store.", null, 1);
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
