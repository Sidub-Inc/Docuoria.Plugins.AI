#load "_common.csx"

#nullable enable

// LIC — delete the stored license key, mirrors `docuoria license remove`.
// Args: (none)
// The DOCUORIA_LICENSE environment variable is read-only and is NOT affected.
// stdout: { status: "ok", removed, path }

try
{
    Cli.Help(Args, "license-remove.csx", "Delete the locally stored Docuoria license key");

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: false);
    var store = ScriptHost.GetLicenseStore(host);
    var removed = store.Delete();

    JsonOut.Write(new
    {
        status = "ok",
        removed,
        path = store.ResolvePath(),
        note = Environment.GetEnvironmentVariable(Docuoria.Licensing.LicenseKeyStore.LicenseEnvironmentVariable) is not null
            ? "The DOCUORIA_LICENSE environment variable is still set and takes precedence."
            : null,
    });
}
catch (Exception ex)
{
    JsonOut.Fail(ex);
}
