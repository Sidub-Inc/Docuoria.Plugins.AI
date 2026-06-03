#load "_common.csx"

#nullable enable

// SCR-08 — enumerate ITemplateStoreProvider.ListAsync.
// stdout: { templates: [..ids..] }

using Docuoria.Storage;

try
{
    Cli.Help(Args, "list-templates.csx", "List all template IDs in the configured store",
        ("store-path", false, "Local template store directory (default: ./templates)", false),
        ("store-url", false, "API template store URL (mutually exclusive with --store-path)", false),
        ("store-key", false, "Function key for API store authentication", false));

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: true);
    var store = ScriptHost.GetStore(host);
    if (store is null)
    {
        JsonOut.Error("no-store", "ITemplateStoreProvider is not registered. Pass --store-path <dir> to specify a local template store directory.", null, 1);
    }

    var ids = new List<string>();
    await foreach (var id in store!.ListAsync())
    {
        ids.Add(id);
    }
    JsonOut.Write(new { templates = ids });
}
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
