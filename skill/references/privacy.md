# Local-processing privacy guarantee

## Claim

When you call `Docuoria`, the PDF bytes you supply never leave the machine the engine runs on. The library reads, extracts, transforms, and renders the PDF entirely in-process. The only outbound network call any first-party component makes is template JSON read/write against an HTTP template store, and that call is opt-in.

## Evidence — extraction is in-process

Every PDF-consuming primitive on `IDocuoriaEngine` (`InspectAsync`, `TestPatternAsync`, `TestGroupsAsync`, `DryRunAsync`, `ExecuteTemplateAsync`, `EvaluateMatchAsync`, `EvaluateMatchRuleAsync`, `ClassifyAsync`, `ClassifyRankedAsync`) takes a `Stream` — never a URL or remote handle for the PDF. See `src/libs/Docuoria/Contracts/IDocuoriaEngine.cs`; each method's documentation carries the contract phrase "The PDF stream is opened and disposed within the call (D-13)." The implementation in `src/libs/Docuoria/Engine/DocuoriaEngine.cs` resolves the in-process `IPdfDocumentFactory` and walks the configured extraction/transformation/publish steps without leaving the process.

## Evidence — the one network surface

`src/libs/Docuoria/Storage/ApiTemplateStoreProvider.cs` implements `ITemplateStoreProvider` and uses `IHttpClientFactory` (named client `ApiTemplateStoreProvider.HttpClientName = "Docuoria.TemplateStore"`) to read and write *templates*. Templates are JSON describing how to extract — they do not contain PDF bytes. A host that wants entirely local processing simply does not register `ApiTemplateStoreProvider`; the engine functions identically against a local store.

`src/libs/Docuoria/Pipeline/Retrieval/Http/HttpRetrievalProvider.cs` is an inbound fetch path the *host* opts into for retrieval steps; it does not upload PDFs supplied by the caller — it only downloads PDFs the template explicitly references.

## What this does NOT promise

- If you wire a third-party logger, telemetry sink, or background storage handler around the engine, your hosting code may transmit data. The guarantee is about the library, not your host.
- If the host uses `HttpRetrievalProvider` to download a PDF before processing, the URL of that PDF is necessarily known to the network. The guarantee is about what happens *after* the engine has the bytes in memory.
- If you store templates via `ApiTemplateStoreProvider`, template content (which may contain regex patterns derived from the PDF's text) crosses the network. PDFs do not.

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
2. Confirm `IDocuoriaEngine` only accepts `Stream` for PDF input — open `src/libs/Docuoria/Contracts/IDocuoriaEngine.cs` and read every method signature.
3. Run `dotnet test` with the network blocked (e.g. firewall rule) and observe extraction tests still pass.
