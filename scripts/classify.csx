#load "_common.csx"

#nullable enable

// CLS-02 — ranked classification: evaluates every stored template and returns top matches
// sorted by ClassificationScore (descending), with full score breakdown.
// Uses ClassifyRankedAsync to open the PDF once and evaluate all templates without
// redundant PDF parsing per template.
// Args: --pdf <path> [--top N]
// stdout: { matches: [ { templateId, classificationScore, requirementsSatisfied, specificityScore, matchQuantityScore, coverageScore, ruleConfidence }, ... ] }

using Docuoria.Contracts;
using Docuoria.Storage;

try
{
    Cli.Help(Args, "classify.csx", "Classify a PDF against stored templates (ranked by confidence)",
        ("pdf", true, "Path to the source PDF", false),
        ("top", false, "Maximum number of results to return (default: 5)", false),
        ("store-path", false, "Local template store directory (default: ./templates)", false),
        ("store-url", false, "API template store URL (mutually exclusive with --store-path)", false),
        ("store-key", false, "Function key for API store authentication", false));

    var pdfPath = Cli.Require(Args, "pdf");
    var topN = int.TryParse(Cli.Get(Args, "top"), out var n) ? n : 5;

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: true);
    var engine = ScriptHost.GetEngine(host);

    using var pdf = LoadPdf(pdfPath);

    // OPT: Single engine call opens the PDF once and evaluates all templates internally,
    // replacing the previous per-template loop that re-parsed the PDF for each template.
    var classifications = await engine.ClassifyRankedAsync(pdf, topN);

    var ranked = classifications
        .Select(c => new
        {
            templateId = c.TemplateIdentifier,
            recommendation = c.Recommendation switch
            {
                Docuoria.Results.ClassificationRecommendation.Strong => "strong",
                Docuoria.Results.ClassificationRecommendation.Partial => "partial",
                _ => "no-match",
            },
            classificationScore = Math.Round(c.ClassificationScore, 4),
            requirementsSatisfied = c.RequirementsSatisfied,
            specificityScore = Math.Round(c.SpecificityScore, 4),
            matchQuantityScore = Math.Round(c.MatchQuantityScore, 4),
            coverageScore = Math.Round(c.CoverageScore, 4),
            ruleConfidence = Math.Round(c.RuleConfidence, 4),
            ambiguity = c.Ambiguity,
        })
        .ToArray();

    var opts = new System.Text.Json.JsonSerializerOptions(Docuoria.Serialization.DocuoriaJsonOptions.Default)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
    JsonOut.WriteRaw(System.Text.Json.JsonSerializer.Serialize(new { matches = ranked }, opts));
}
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
