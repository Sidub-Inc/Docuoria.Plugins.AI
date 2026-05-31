#load "_common.csx"

#nullable enable

// SCR-03 — Wrapper over IDocuoriaEngine.TestGroupsAsync.
// Args: --pattern <regex> --pdf <path> [--page <n>]
// stdout: PatternGroupTestResult JSON.

using Docuoria.Configuration;
using Docuoria.Contracts;
using Docuoria.Models;

try
{
    Cli.Help(Args, "test-groups.csx", "Test each capture group of a regex independently",
        ("pdf", true, "Path to the source PDF", false),
        ("pattern", true, "Multi-group regex pattern", false),
        ("page", false, "1-based page index (default: all pages)", false));

    var pattern = Cli.Require(Args, "pattern");
    var pdfPath = Cli.Require(Args, "pdf");
    var pageStr = Cli.Get(Args, "page");

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: false);
    var engine = ScriptHost.GetEngine(host);

    PatternTestOptions? options = null;
    if (!string.IsNullOrWhiteSpace(pageStr))
    {
        if (!int.TryParse(pageStr, out var page) || page < 1)
        {
            JsonOut.Error("bad-arg", "--page must be a positive integer", null, 2);
        }
        options = new PatternTestOptions { PageFilter = PageFilter.SinglePage(page) };
    }

    using var pdf = LoadPdf(pdfPath);
    var result = await engine.TestGroupsAsync(pdf, pattern, options);
    JsonOut.Write(result);
}
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
