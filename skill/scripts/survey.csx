#load "_common.csx"

#nullable enable

// Survey script — wraps PdfCorpusSurvey to report structural facts about a directory of PDFs.
// Args: --corpus <dir> [--store-path <dir> | --store-url <url>] [--strict]
// stdout: { pdfCount, matchedGroups: [ { template, pdfs[], representative } ],
//           unmatched: [ { pdf, pageCount, structuralTokens[] } ], guidance }
// Exit codes: 0 = normal, 1 = insufficient corpus / invalid input,
//             2 = --strict and unmatched PDFs are present (manual grouping decision required)

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Docuoria.Engine.Survey;

try
{
    Cli.Help(Args, "survey.csx", "Survey a PDF corpus and produce structural groupings",
        ("corpus", true, "Path to the directory containing corpus PDFs", false),
        ("store-path", false, "Local template store directory (default: ./templates)", false),
        ("store-url", false, "API template store URL (mutually exclusive with --store-path)", false),
        ("store-key", false, "Function key for API store authentication", false),
        ("strict", false, "Exit 2 when any PDF is unmatched (a grouping/authoring decision is required)", true));

    var corpusDir = Cli.Require(Args, "corpus");
    bool strict = Cli.Has(Args, "strict");

    if (!Directory.Exists(corpusDir))
    {
        JsonOut.Error("corpus-not-found", $"Corpus directory not found at '{corpusDir}'", null, 1);
    }

    var pdfPaths = Directory.GetFiles(corpusDir, "*.pdf", SearchOption.TopDirectoryOnly)
        .Concat(Directory.GetFiles(corpusDir, "*.PDF", SearchOption.TopDirectoryOnly))
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (pdfPaths.Count < 2)
    {
        JsonOut.Error("insufficient-corpus",
            $"Corpus at '{corpusDir}' contains {pdfPaths.Count} PDF(s). At least 2 are required.",
            "For a single-PDF workflow, use inspect.csx + test-pattern.csx directly without survey.",
            1);
    }

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: true);
    var engine = ScriptHost.GetEngine(host);

    var survey = new PdfCorpusSurvey(engine);

    SurveyResult result;
    try
    {
        result = await survey.RunAsync(pdfPaths);
    }
    catch (ArgumentException ex)
    {
        JsonOut.Error("insufficient-corpus", ex.Message, null, 1);
        return; // unreachable
    }

    var envelope = new
    {
        pdfCount = result.PdfCount,
        matchedGroups = result.MatchedGroups.Select(g => new
        {
            template = g.Template,
            pdfs = g.Pdfs,
            representative = g.Representative,
        }).ToArray(),
        unmatched = result.Unmatched.Select(p => new
        {
            pdf = p.Pdf,
            pageCount = p.PageCount,
            structuralTokens = p.StructuralTokens,
        }).ToArray(),
        guidance = result.Guidance,
    };

    JsonOut.Write(envelope);

    if (strict && result.Unmatched.Count > 0)
    {
        // Exit 2 after emitting the full envelope: unmatched PDFs need a human/agent
        // grouping decision before templates are authored.
        Environment.Exit(2);
    }
}
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
