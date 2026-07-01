#load "_common.csx"

#nullable enable

// LIC — self-serve free license acquisition, mirrors `docuoria license acquire`.
// Args: --email <address>
// Free offerings complete inline (credential stored automatically); paid offerings
// return a browser checkout URL — complete it there and store the key via license-set.csx.
// stdout: { status: "ok", stored, licenseId } | { status: "checkout-required", checkoutUrl }

try
{
    Cli.Help(Args, "license-acquire.csx", "Acquire a free Docuoria license for an email address",
        ("email", true, "Email address the license is issued to", false));

    var email = Cli.Require(Args, "email");

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: false);
    var acquisition = ScriptHost.GetLicenseAcquisition(host);

    try
    {
        var result = await acquisition.AcquireFreeAsync(email);
        if (result.LicenseStored)
        {
            JsonOut.Write(new
            {
                status = "ok",
                stored = true,
                licenseId = result.LicenseId,
                note = "Run license-status.csx to inspect the license.",
            });
        }
        else if (result.CheckoutUrl is not null)
        {
            JsonOut.Write(new
            {
                status = "checkout-required",
                checkoutUrl = result.CheckoutUrl,
                note = "Complete checkout in the browser, then store the issued key with license-set.csx.",
            });
        }
        else
        {
            JsonOut.Error("acquire-failed", result.Error ?? "Acquisition failed.", null, 1);
        }
    }
    catch (InvalidOperationException ex)
    {
        // Catalog not yet provisioned — deterministic marketplace fallback guidance.
        JsonOut.Error("not-provisioned", ex.Message,
            "Obtain a key from the marketplace and store it with license-set.csx.", 1);
    }
}
catch (Exception ex)
{
    JsonOut.Fail(ex);
}
