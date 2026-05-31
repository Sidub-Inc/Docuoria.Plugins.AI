#load "_common.csx"

#nullable enable

// SCR-08 — enumerate ITemplateStoreProvider.ListAsync.
// stdout: { templates: [..ids..] }

using Docuoria.Storage;

try
{
    Cli.Help(Args, "list-templates.csx", "List all template IDs in the configured store");

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: true);
    var store = ScriptHost.GetStore(host);
    if (store is null)
    {
        JsonOut.Error("no-store", "ITemplateStoreProvider is not registered. Set the DOCUORIA_STORE_LOCAL_PATH environment variable to a directory path to enable the local template store.", null, 1);
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
