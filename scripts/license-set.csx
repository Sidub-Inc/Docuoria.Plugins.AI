#load "_common.csx"

#nullable enable

// LIC — store an encoded license credential, mirrors `docuoria license set`.
// Args: --key <encoded-credential>
// The credential value is NEVER echoed back or logged; it is stored only in the local
// key store (~/.docuoria/license.json, honoring DOCUORIA_HOME).
// stdout: { status: "ok", stored: true, verified, path }

try
{
    Cli.Help(Args, "license-set.csx", "Store an encoded Docuoria license credential",
        ("key", true, "The encoded license credential to store (never echoed back)", false));

    var key = Cli.Require(Args, "key");

    // Validate the credential parses BEFORE persisting anything.
    try
    {
        _ = Sidub.Licensing.Context.LicensingContext.FromEncodedString(key);
    }
    catch (Exception)
    {
        JsonOut.Error("invalid-credential",
            "The supplied credential could not be parsed; nothing was stored.",
            "Check that the full encoded key was pasted intact.", 2);
    }

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: false);
    var store = ScriptHost.GetLicenseStore(host);
    await store.SaveAsync(key);

    // Verify the stored key yields a license (advisory — offline is not a failure).
    var status = await ScriptHost.GetLicenseInfo(host).GetStatusAsync();

    JsonOut.Write(new
    {
        status = "ok",
        stored = true,
        verified = status.IsLicensed,
        licenseId = status.LicenseId,
        path = store.ResolvePath(),
        note = status.IsLicensed
            ? null
            : "Stored, but the license could not be verified right now (service unreachable or key not yet active).",
    });
}
catch (Exception ex)
{
    JsonOut.Fail(ex);
}
