#load "_common.csx"

#nullable enable

// LIC — license status surface, mirrors `docuoria license status`.
// Args: (none)
// stdout: { licensed, credentialPresent, credentialSource, licenseId, expiry,
//           features, consumption: [{ featureKey, currentUsage, limit, windowSeconds }], marketplaceUrl }
// Exit 0 always — "unlicensed" is a reportable state, not an error.

try
{
    Cli.Help(Args, "license-status.csx", "Show the Docuoria license state, features, and usage");

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: false);
    var info = ScriptHost.GetLicenseInfo(host);
    var status = await info.GetStatusAsync();

    JsonOut.Write(new
    {
        licensed = status.IsLicensed,
        credentialPresent = status.CredentialPresent,
        credentialSource = status.CredentialSource,
        licenseId = status.LicenseId,
        expiry = status.Expiry,
        features = status.Features,
        consumption = status.Consumption.Select(c => new
        {
            featureKey = c.FeatureKey,
            currentUsage = c.CurrentUsage,
            limit = c.Limit,
            unlimited = c.Limit == 0,
            windowSeconds = c.WindowSeconds,
            percentage = c.Percentage,
        }),
        marketplaceUrl = status.MarketplaceUrl,
        purchaseUrl = status.PurchaseUrl,
        guidance = status.IsLicensed
            ? null
            : "Get a free license with 'dotnet script scripts/license-acquire.csx', " +
              $"or get one at {status.PurchaseUrl} and store it with 'dotnet script scripts/license-set.csx -- --key <key>'.",
    });
}
catch (Exception ex)
{
    JsonOut.Fail(ex);
}
