#load "_common.csx"

#nullable enable

// LIC — free license acquisition from the script channel.
// Args: (none)
// The scripts carry no buyer sign-in (device code needs a person at a browser), so from here the
// journey is: this script exits 1 `checkout-unavailable` with the purchase URL in `detail`; the
// agent sends the user there for a free key and stores it with license-set.csx. A paid offering
// would return `checkout-required` with a browser checkout URL. The "ok / stored" branch below is
// reached only by a host that registers buyer sign-in (the `docuoria license acquire` CLI verb).
// stdout: { status: "ok", stored, licenseId } | { status: "checkout-required", checkoutUrl }
// stderr: { error: { code: "checkout-unavailable", message, detail: <purchase URL + next step> } } exit 1

try
{
    Cli.Help(Args, "license-acquire.csx", "Acquire a free Docuoria license");

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: false);
    var acquisition = ScriptHost.GetLicenseAcquisition(host);

    try
    {
        var result = await acquisition.AcquireFreeAsync();
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
    catch (Exception ex)
    {
        // Mapped in _common.csx: this script cannot name SDK exception types (the #r lives there),
        // and trying to do so is what made this script fail to compile at all.
        JsonOut.FailAcquisition(ex);
    }
}
catch (Exception ex)
{
    JsonOut.Fail(ex);
}
