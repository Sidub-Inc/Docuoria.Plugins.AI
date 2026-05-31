#load "_common.csx"

#nullable enable

// SCR-06 — Wrapper over IDocuoriaEngine.ExecuteTemplateAsync<TGenerator, TOptions>.
// Args: --pdf <path> --template <file.json> --format csv|json [--output <path>]
// Success: SucceededResult → write payload bytes (to --output or wrap as string in stdout JSON).
// Rejected/Failed: emit { status, result } and exit 1.

using System.Text;
using Docuoria.Configuration;
using Docuoria.Contracts;
using Docuoria.Models;
using Docuoria.Output.Csv;
using Docuoria.Output.Json;
using Docuoria.Results;

try
{
    Cli.Help(Args, "execute.csx", "Full pipeline run with output generation (CSV or JSON)",
        ("pdf", true, "Path to the source PDF", false),
        ("template", true, "Path to the template JSON file", false),
        ("format", true, "Output format: csv or json", false),
        ("output", false, "Write output to this file path (default: stdout)", false));

    var pdfPath = Cli.Require(Args, "pdf");
    var templatePath = Cli.Require(Args, "template");
    var format = Cli.Require(Args, "format").Trim().ToLowerInvariant();
    var outputPath = Cli.Get(Args, "output");

    if (format != "csv" && format != "json")
    {
        JsonOut.Error("bad-format", "expected csv|json", null, 2);
    }
    if (!File.Exists(templatePath))
    {
        JsonOut.Error("template-not-found", $"Template not found at '{templatePath}'", null, 1);
    }

    var template = Template.FromJson(File.ReadAllText(templatePath));

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: false);
    var engine = ScriptHost.GetEngine(host);

    using var pdf = LoadPdf(pdfPath);

    ProcessingResult result = format switch
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
            var payload = ok.Output.Payload;
            if (!string.IsNullOrEmpty(outputPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath!))!);
                await File.WriteAllBytesAsync(outputPath!, payload.ToArray());
                JsonOut.Write(new { status = "ok", path = outputPath });
            }
            else
            {
                var text = Encoding.UTF8.GetString(payload.Span);
                JsonOut.Write(new { status = "ok", format, output = text });
            }
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
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
