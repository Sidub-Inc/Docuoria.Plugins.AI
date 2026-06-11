#load "_common.csx"

#nullable enable

// Regression-check script — compares a baseline and modified template against a corpus.
// Args: --baseline <template.json> | (--baseline-id <id> --store-path <dir>)
//       --modified <template.json>
//       --corpus <dir>
//       [--store-path <dir> | --store-url <url>]
// stdout: { pdfs[], summary: { regressionsDetected, improvementsDetected, unchanged } }
// Exit codes: 0 = no regressions, 1 = invalid input, 2 = regressions detected

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Docuoria.Contracts;
using Docuoria.Engine.Regression;
using Docuoria.Models;

try
{
    Cli.Help(Args, "regression-check.csx", "Compare baseline vs modified template across a PDF corpus",
        ("baseline", false, "Path to the baseline template JSON file", false),
        ("baseline-id", false, "Template ID to load from the store as baseline", false),
        ("modified", true, "Path to the modified template JSON file", false),
        ("corpus", true, "Path to the directory containing corpus PDFs", false),
        ("store-path", false, "Local template store directory (default: ./templates)", false),
        ("store-url", false, "API template store URL (mutually exclusive with --store-path)", false),
        ("store-key", false, "Function key for API store authentication", false));

    var baselinePath = Cli.Get(Args, "baseline");
    var baselineId = Cli.Get(Args, "baseline-id");
    var modifiedPath = Cli.Require(Args, "modified");
    var corpusDir = Cli.Require(Args, "corpus");

    // Validate mutually exclusive baseline sources.
    if (!string.IsNullOrWhiteSpace(baselinePath) && !string.IsNullOrWhiteSpace(baselineId))
    {
        JsonOut.Error("ambiguous-baseline",
            "--baseline and --baseline-id are mutually exclusive. Specify exactly one.",
            null, 1);
    }

    if (string.IsNullOrWhiteSpace(baselinePath) && string.IsNullOrWhiteSpace(baselineId))
    {
        JsonOut.Error("missing-arg", "Either --baseline or --baseline-id is required.", null, 1);
    }

    if (!Directory.Exists(corpusDir))
    {
        JsonOut.Error("corpus-not-found", $"Corpus directory not found at '{corpusDir}'", null, 1);
    }

    if (!File.Exists(modifiedPath))
    {
        JsonOut.Error("template-not-found", $"Modified template not found at '{modifiedPath}'", null, 1);
    }

    var pdfPaths = Directory.GetFiles(corpusDir, "*.pdf", SearchOption.TopDirectoryOnly)
        .Concat(Directory.GetFiles(corpusDir, "*.PDF", SearchOption.TopDirectoryOnly))
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (pdfPaths.Count == 0)
    {
        JsonOut.Error("empty-corpus", $"No PDF files found in corpus directory '{corpusDir}'.", null, 1);
    }

    var modified = Template.FromJson(File.ReadAllText(modifiedPath));

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: true);
    var engine = ScriptHost.GetEngine(host);

    // Resolve baseline template.
    Template? baseline;
    if (!string.IsNullOrWhiteSpace(baselinePath))
    {
        if (!File.Exists(baselinePath))
        {
            JsonOut.Error("template-not-found", $"Baseline template not found at '{baselinePath}'", null, 1);
        }
        baseline = Template.FromJson(File.ReadAllText(baselinePath));
    }
    else
    {
        var store = ScriptHost.GetStore(host);
        if (store is null)
        {
            JsonOut.Error("no-store", "ITemplateStoreProvider is not registered. Use --store-path or --store-url.", null, 1);
        }
        baseline = await store!.LoadAsync(baselineId!);
        if (baseline is null)
        {
            JsonOut.Error("not-found", $"Template '{baselineId}' not found in store.", null, 1);
        }
    }

    var differ = new TemplateRegressionDiff(engine);
    var diff = await differ.RunAsync(baseline!, modified, pdfPaths);

    var envelope = new
    {
        pdfs = diff.Pdfs.Select(e => new
        {
            pdfPath = e.PdfPath,
            baselineClassificationScore = Math.Round(e.BaselineClassificationScore, 4),
            modifiedClassificationScore = Math.Round(e.ModifiedClassificationScore, 4),
            baselineRequirementsSatisfied = e.BaselineRequirementsSatisfied,
            modifiedRequirementsSatisfied = e.ModifiedRequirementsSatisfied,
            scalarDiffs = e.ScalarDiffs.Select(d => new { fieldName = d.FieldName, baseline = d.Baseline, modified = d.Modified }).ToArray(),
            collectionDiffs = e.CollectionDiffs.Select(d => new { collectionName = d.CollectionName, baselineRowCount = d.BaselineRowCount, modifiedRowCount = d.ModifiedRowCount }).ToArray(),
            isRegression = e.IsRegression,
            isImprovement = e.IsImprovement,
        }).ToArray(),
        summary = new
        {
            regressionsDetected = diff.Summary.RegressionsDetected,
            improvementsDetected = diff.Summary.ImprovementsDetected,
            unchanged = diff.Summary.Unchanged,
        },
    };

    JsonOut.Write(envelope);

    if (diff.Summary.RegressionsDetected > 0)
    {
        Environment.Exit(2);
    }
}
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
