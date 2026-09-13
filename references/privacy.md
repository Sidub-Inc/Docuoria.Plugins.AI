# Local-processing privacy guarantee

## Claim

When you call `Docuoria`, the PDF bytes you supply never leave the machine the engine runs on. The library reads, extracts, transforms, and renders the PDF entirely in-process — by design, and asserted by test (`tests/Docuoria.Tests/Hosts/PrivacyInvariantTests.cs`).

Two things do cross the network, and neither carries document content:

- **Licensing.** Since licensing was added, the SDK contacts Monaiq (`https://api.monaiq.com`, or `https://api.uat.monaiq.com` when `DOCUORIA_ENVIRONMENT=Uat`) to validate the licence and to report aggregated per-feature usage counts. What is sent is the licence identifier, which feature keys were consumed, how many units, and when; usage is aggregated before it is sent and transmitted periodically and when the process exits. It never includes PDF content, extracted values, file names, or template content (Privacy Policy, `PRIVACY.txt` § 2). `DOCUORIA_ENFORCEMENT=Disabled` turns licensing off for local development, and with it these calls.
- **Template store (opt-in).** Template JSON read/write against an HTTP template store, only when a host registers `ApiTemplateStoreProvider`. The default local file store makes no network call.

## Evidence — extraction is in-process

Every PDF-consuming primitive on `IDocuoriaEngine` (`InspectAsync`, `TestPatternAsync`, `TestGroupsAsync`, `DryRunAsync`, `ExecuteTemplateAsync`, `EvaluateMatchAsync`, `EvaluateMatchRuleAsync`, `ClassifyAsync`, `ClassifyRankedAsync`) takes a `Stream` — never a URL or remote handle for the PDF. See `src/libs/Docuoria/Contracts/IDocuoriaEngine.cs`; each method's documentation carries the contract phrase "The PDF stream is opened and disposed within the call (D-13)." The implementation in `src/libs/Docuoria/Engine/DocuoriaEngine.cs` resolves the in-process `IPdfDocumentFactory` and walks the configured extraction/transformation/publish steps without leaving the process.

## Evidence — the network surfaces

**Template store.** `src/libs/Docuoria/Storage/ApiTemplateStoreProvider.cs` implements `ITemplateStoreProvider` and uses `IHttpClientFactory` (named client `ApiTemplateStoreProvider.HttpClientName = "Docuoria.TemplateStore"`) to read and write *templates*. Templates are JSON describing how to extract — they do not contain PDF bytes. A host that wants no template traffic simply does not register `ApiTemplateStoreProvider`; the engine functions identically against a local store.

**Retrieval (host opt-in).** `src/libs/Docuoria/Pipeline/Retrieval/Http/HttpRetrievalProvider.cs` is an inbound fetch path the *host* opts into for retrieval steps; it does not upload PDFs supplied by the caller — it only downloads PDFs the template explicitly references.

**Licensing.** `src/libs/Docuoria/Registration/LicensingServiceCollectionExtensions.cs` registers the Sidub licensing client (`AddSidubLicensing<DocuoriaLicensingContextProvider>()`) against `<api-base>/licensing` (licence validation) and `<api-base>/consumption` (aggregated usage), and the buyer-facing purchase client (`AddSidubLicensingPurchaseClient`) against `<api-base>/purchase` — catalog reads are anonymous, and checkout additionally needs a sign-in that only a host supplies (`AddDocuoriaBuyerSignIn`). `<api-base>` is `DocuoriaLicenseOptions.ApiBaseUri` (`https://api.monaiq.com` by default; `DOCUORIA_API_BASE` or `DOCUORIA_ENVIRONMENT` move it). The engine's side of that boundary is `IDocuoriaLicenseGuard` (`src/libs/Docuoria/Licensing/`), which is handed a feature key and a unit count — never a PDF, a record, or a template. Without `AddDocuoriaLicensing` the engine runs with `NoOpLicenseGuard` and makes no licensing call at all.

## What this does NOT promise

- If you wire a third-party logger, telemetry sink, or background storage handler around the engine, your hosting code may transmit data. The guarantee is about the library, not your host.
- If the host uses `HttpRetrievalProvider` to download a PDF before processing, the URL of that PDF is necessarily known to the network. The guarantee is about what happens *after* the engine has the bytes in memory.
- If you store templates via `ApiTemplateStoreProvider`, template content (which may contain regex patterns derived from the PDF's text) crosses the network. PDFs do not.
- With licensing enabled, Monaiq learns that *a* licence ran *a* feature *n* times at *t*. It does not learn what the documents were or what came out of them.

## Verifying for yourself

1. Search the library for outbound HTTP usage:
   `Select-String -Path src/libs/Docuoria -Pattern 'HttpClient|HttpRequestMessage' -Recurse`.
   Confirm hits are confined to the template-store and retrieval surfaces:
   - `Storage/ApiTemplateStoreProvider.cs`
   - `Storage/Http/TemplateStoreCredentialHandler.cs`
   - `Registration/HttpRetrievalProviderBuilderExtensions.cs`
   - `Registration/TemplateStoreBuilderExtensions.cs`
   - `Pipeline/Retrieval/Http/HttpRetrievalProvider.cs`

   The extra hits beyond `ApiTemplateStoreProvider` and `HttpRetrievalProvider` are credential-handler and DI-registration support for those same two surfaces — they do not introduce new outbound paths.

   The licensing traffic does **not** appear in this grep: its HTTP client lives inside the `Sidub.Licensing.Client` package. Find where it is wired with `Select-String -Path src/libs/Docuoria -Pattern 'AddSidubLicensing' -Recurse` — the single hit is `Registration/LicensingServiceCollectionExtensions.cs` — and read the three endpoints derived there and the payload contract in `PRIVACY.txt` § 2.
2. Confirm `IDocuoriaEngine` only accepts `Stream` for PDF input — open `src/libs/Docuoria/Contracts/IDocuoriaEngine.cs` and read every method signature.
3. Run `dotnet test` with the network blocked (e.g. firewall rule) and observe extraction tests still pass. The engine test suite does not call `AddDocuoriaLicensing`, so it runs with `NoOpLicenseGuard`.
4. Run any script with `DOCUORIA_ENFORCEMENT=Disabled` and the network blocked: extraction completes and no connection is attempted.
