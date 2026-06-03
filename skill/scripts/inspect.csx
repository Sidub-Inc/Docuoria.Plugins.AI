#load "_common.csx"

#nullable enable

// SCR-01 — Agent-facing wrapper over IDocuoriaEngine.InspectAsync (read-only PDF probe).
// Args: --pdf <path> [--page <n>]
// stdout: PdfInspection JSON
// Exit codes: 0 success, 1 handled error, 2 bad args.

using Docuoria.Configuration;
using Docuoria.Contracts;
using Docuoria.Models;

try
{
    Cli.Help(Args, "inspect.csx", "Inspect PDF structure (page count, text blocks, tables)",
        ("pdf", true, "Path to the source PDF", false),
        ("page", false, "1-based page index (default: all pages)", false));

    var pdfPath = Cli.Require(Args, "pdf");
    var pageStr = Cli.Get(Args, "page");

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: false);
    var engine = ScriptHost.GetEngine(host);

    PageFilter? filter = null;
    if (!string.IsNullOrWhiteSpace(pageStr))
    {
        if (!int.TryParse(pageStr, out var page) || page < 1)
        {
            JsonOut.Error("bad-arg", "--page must be a positive integer", null, 2);
        }
        filter = PageFilter.SinglePage(page);
    }

    using var pdf = LoadPdf(pdfPath);
    var result = await engine.InspectAsync(pdf, filter);
    JsonOut.Write(result);
}
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
