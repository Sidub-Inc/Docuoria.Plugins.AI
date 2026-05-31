#load "_common.csx"

#nullable enable

// SCR-05 — Wrapper over IDocuoriaEngine.DryRunAsync (extraction + transformation only).
// Args: --pdf <path> --template <file.json> [--preview-as csv|json]
// stdout: { kind, result } — discriminator + payload.
// With --preview-as: { kind, result, preview } — adds formatted output string.

using System.Text;
using Docuoria.Configuration;
using Docuoria.Contracts;
using Docuoria.Models;
using Docuoria.Output.Csv;
using Docuoria.Output.Json;
using Docuoria.Results;

try
{
    Cli.Help(Args, "dry-run.csx", "Run extraction + transformation without output generation",
        ("pdf", true, "Path to the source PDF", false),
        ("template", true, "Path to the template JSON file", false),
        ("preview-as", false, "Preview formatted output: csv or json (no file written)", false));

    var pdfPath = Cli.Require(Args, "pdf");
    var templatePath = Cli.Require(Args, "template");
    var previewAs = Cli.Get(Args, "preview-as")?.Trim().ToLowerInvariant();

    if (previewAs is not null && previewAs != "csv" && previewAs != "json")
    {
        JsonOut.Error("bad-format", "--preview-as must be csv or json", null, 2);
    }

    if (!File.Exists(templatePath))
    {
        JsonOut.Error("template-not-found", $"Template not found at '{templatePath}'", null, 1);
    }

    var template = Template.FromJson(File.ReadAllText(templatePath));

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: false);
    var engine = ScriptHost.GetEngine(host);

    using var pdf = LoadPdf(pdfPath);

    if (previewAs is null)
    {
        // Standard dry-run: extraction + transformation only.
        var result = await engine.DryRunAsync(pdf, template);
        JsonOut.Write(new { kind = result.GetType().Name, result });
    }
    else
    {
        // Preview mode: full execute, but output goes to stdout preview instead of disk.
        ProcessingResult result = previewAs switch
        {
            "csv" => await engine.ExecuteTemplateAsync<CsvOutputGenerator, CsvGeneratorOptions>(
                pdf, template, new CsvGeneratorOptions()),
            "json" => await engine.ExecuteTemplateAsync<JsonOutputGenerator, JsonGeneratorOptions>(
                pdf, template, new JsonGeneratorOptions()),
            _ => throw new InvalidOperationException("unreachable")
        };

        switch (result)
        {
            case SucceededResult ok:
                var preview = Encoding.UTF8.GetString(ok.Output.Payload.Span);
                JsonOut.Write(new { kind = "SucceededResult", format = previewAs, preview });
                break;
            case RejectedResult rej:
                JsonOut.Error("rejected", $"Rejected ({rej.Reason}){(rej.Detail is not null ? $": {rej.Detail}" : "")}", null, 1);
                break;
            case FailedResult fail:
                JsonOut.Error("failed", fail.ErrorMessage, fail.InnerDetail, 1);
                break;
            default:
                JsonOut.Error("unknown-result", result.GetType().Name, null, 1);
                break;
        }
    }
}
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
