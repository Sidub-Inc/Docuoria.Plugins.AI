#load "_common.csx"

#nullable enable

// SCR-03 — Wrapper over IDocuoriaEngine.TestGroupsAsync.
// Args: --pattern <regex> --pdf <path> [--page <n>] [--timeout-ms <n>]
// stdout: PatternGroupTestResult JSON.
// F13: a default 5s regex match timeout bounds catastrophic backtracking; on timeout the
// script exits 1 with the standard error envelope (code "pattern-timeout").

using Docuoria.Configuration;
using Docuoria.Contracts;
using Docuoria.Models;
using Docuoria.Results;

try
{
    Cli.Help(Args, "test-groups.csx", "Test each capture group of a regex independently",
        ("pdf", true, "Path to the source PDF", false),
        ("pattern", true, "Multi-group regex pattern", false),
        ("page", false, "1-based page index (default: all pages)", false),
        ("timeout-ms", false, "Regex match timeout in milliseconds (default: 5000)", false));

    var pattern = Cli.Require(Args, "pattern");
    var pdfPath = Cli.Require(Args, "pdf");
    var pageStr = Cli.Get(Args, "page");
    var timeoutStr = Cli.Get(Args, "timeout-ms");

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: false);
    var engine = ScriptHost.GetEngine(host);

    PageFilter? pageFilter = null;
    if (!string.IsNullOrWhiteSpace(pageStr))
    {
        if (!int.TryParse(pageStr, out var page) || page < 1)
        {
            JsonOut.Error("bad-arg", "--page must be a positive integer", null, 2);
        }
        pageFilter = PageFilter.SinglePage(page);
    }

    // F13: default 5s match timeout at the script surface (engine default stays Infinite).
    var timeoutMs = 5000;
    if (!string.IsNullOrWhiteSpace(timeoutStr))
    {
        if (!int.TryParse(timeoutStr, out timeoutMs) || timeoutMs < 1)
        {
            JsonOut.Error("bad-arg", "--timeout-ms must be a positive integer", null, 2);
        }
    }

    var options = new PatternTestOptions
    {
        PageFilter = pageFilter,
        MatchTimeout = TimeSpan.FromMilliseconds(timeoutMs),
    };

    using var pdf = LoadPdf(pdfPath);
    var result = await engine.TestGroupsAsync(pdf, pattern, options);
    if (result.Error is { Kind: PatternTestErrorKind.Timeout })
    {
        JsonOut.Error(
            "pattern-timeout",
            $"Pattern matching exceeded the {timeoutMs} ms match timeout. The pattern likely backtracks catastrophically — make it more deterministic (avoid nested ambiguous quantifiers, bound repetition, anchor match starts) or raise --timeout-ms.",
            result.Error.Message,
            1);
    }
    JsonOut.Write(result);
}
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
