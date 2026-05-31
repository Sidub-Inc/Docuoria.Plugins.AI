#load "_common.csx"

#nullable enable

// SCR-02 — Wrapper over IDocuoriaEngine.TestPatternAsync.
// Args: --pattern <regex> --pdf <path> [--page <n>] [--block-separator <str>]
// stdout: PatternTestResult JSON (Error round-trips per Phase 24 — do NOT swallow).

using Docuoria.Configuration;
using Docuoria.Contracts;
using Docuoria.Models;

try
{
    Cli.Help(Args, "test-pattern.csx", "Test a regex pattern against PDF text",
        ("pdf", true, "Path to the source PDF", false),
        ("pattern", true, "Regex pattern to test", false),
        ("page", false, "1-based page index (default: all pages)", false),
        ("block-separator", false, "Override block separator for text flattening", false));

    var pattern = Cli.Require(Args, "pattern");
    var pdfPath = Cli.Require(Args, "pdf");
    var blockSep = Cli.Get(Args, "block-separator");
    var pageStr = Cli.Get(Args, "page");

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: false);
    var engine = ScriptHost.GetEngine(host);

    PatternTestOptions? options = null;

    // Build options from optional flags
    string? blockSepValue = blockSep;
    PageFilter? pageFilter = null;

    if (!string.IsNullOrWhiteSpace(pageStr))
    {
        if (!int.TryParse(pageStr, out var page) || page < 1)
        {
            JsonOut.Error("bad-arg", "--page must be a positive integer", null, 2);
        }
        pageFilter = PageFilter.SinglePage(page);
    }

    if (!string.IsNullOrEmpty(blockSepValue) || pageFilter is not null)
    {
        options = new PatternTestOptions
        {
            BlockSeparator = blockSepValue ?? "\n",
            PageFilter = pageFilter
        };
    }

    using var pdf = LoadPdf(pdfPath);

    var result = await engine.TestPatternAsync(pdf, pattern, options);
    JsonOut.Write(result);
}
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
