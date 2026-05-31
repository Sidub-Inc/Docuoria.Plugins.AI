#load "_common.csx"

#nullable enable

// SCR-07 + CLS-01 helper — wrapper over IDocuoriaEngine.EvaluateMatchAsync.
// Args: --pdf <path> --template <id-or-file>
// Auto-detects template source per Area-3: contains path sep OR ends .json OR File.Exists -> file;
// otherwise loads from store via ITemplateStoreProvider.LoadAsync(value).
// stdout: { confidence, matchedRules } JSON.

using Docuoria.Contracts;
using Docuoria.Models;
using Docuoria.Storage;

try
{
    Cli.Help(Args, "evaluate-match.csx", "Evaluate a template's match rule against a PDF",
        ("pdf", true, "Path to the source PDF", false),
        ("template", true, "Template ID or path to template JSON file", false));

    var pdfPath = Cli.Require(Args, "pdf");
    var templateRef = Cli.Require(Args, "template");

    bool looksLikeFile =
        templateRef.IndexOfAny(new[] { '/', '\\' }) >= 0 ||
        templateRef.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
        File.Exists(templateRef);

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: true);
    var engine = ScriptHost.GetEngine(host);

    Template? template;
    if (looksLikeFile)
    {
        if (!File.Exists(templateRef))
        {
            JsonOut.Error("template-not-found", $"Template file not found at '{templateRef}'", null, 1);
        }
        template = Template.FromJson(File.ReadAllText(templateRef));
    }
    else
    {
        var store = ScriptHost.GetStore(host);
        if (store is null)
        {
            JsonOut.Error("no-store", "ITemplateStoreProvider is not registered.", null, 1);
        }
        template = await store!.LoadAsync(templateRef);
        if (template is null)
        {
            JsonOut.Error("not-found", $"template '{templateRef}' not found in store", null, 1);
        }
    }

    using var pdf = LoadPdf(pdfPath);
    var evaluation = await engine.EvaluateMatchAsync(template!, pdf);

    // Project a clean LLM-facing response: single aggregated confidence + diagnostic rules.
    // Confidence = ruleConfidence × extractionProbeScore (0.0 when either fails).
    JsonOut.Write(new
    {
        confidence = Math.Round(evaluation.RuleConfidence * evaluation.ExtractionProbeScore, 4),
        matchedRules = evaluation.MatchedRules,
    });
}
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
