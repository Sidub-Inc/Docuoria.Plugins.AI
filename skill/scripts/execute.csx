#load "_common.csx"

#nullable enable

// SCR-06 — Wrapper over IDocuoriaEngine.ExecuteTemplateAsync<TGenerator, TOptions>.
// Args: --pdf <path> --template <file.json> --format csv|json [--output <path>]
//       [--append] [--on-duplicate skip|replace|fail] [--strict-header] [--overwrite]
// Plain mode: SucceededResult → write payload bytes (to --output or wrap as string in stdout JSON).
// Ledger mode (--append): merge this PDF's rows into the ledger at --output (created when missing).
//   Idempotent per source PDF: an already-recorded PDF is skipped (default), replaced, or fails
//   per --on-duplicate — a skipped duplicate never runs the engine. Writes are atomic.
// Safety: --append refuses files that are not recognizable ledgers (`not-a-ledger`); a plain
//   --output refuses to overwrite a recognized ledger without --overwrite (`existing-ledger`).
// Rejected/Failed: emit { status, result } and exit 1.

using System.Text;
using Docuoria.Configuration;
using Docuoria.Contracts;
using Docuoria.Models;
using Docuoria.Output.Csv;
using Docuoria.Output.Json;
using Docuoria.Output.Ledger;
using Docuoria.Results;

try
{
    Cli.Help(Args, "execute.csx", "Full pipeline run with output generation (CSV or JSON)",
        ("pdf", true, "Path to the source PDF", false),
        ("template", true, "Path to the template JSON file", false),
        ("format", true, "Output format: csv or json", false),
        ("output", false, "Write output to this file path (default: stdout)", false),
        ("append", false, "Merge into the ledger at --output (created when missing); idempotent per source PDF", true),
        ("on-duplicate", false, "With --append: skip|replace|fail when this PDF is already recorded (default: skip)", false),
        ("strict-header", false, "With --append + csv: fail instead of adding new columns to the ledger header", true),
        ("overwrite", false, "Allow a plain (non-append) --output to replace an existing Docuoria ledger", true));

    var pdfPath = Cli.Require(Args, "pdf");
    var templatePath = Cli.Require(Args, "template");
    var format = Cli.Require(Args, "format").Trim().ToLowerInvariant();
    var outputPath = Cli.Get(Args, "output");
    var append = Cli.Has(Args, "append");
    var overwrite = Cli.Has(Args, "overwrite");
    var strictHeader = Cli.Has(Args, "strict-header");

    if (format != "csv" && format != "json")
    {
        JsonOut.Error("bad-format", "expected csv|json", null, 2);
    }
    if (append && string.IsNullOrEmpty(outputPath))
    {
        JsonOut.Error("append-requires-output", "--append requires --output <path> (the ledger file to extend).", null, 2);
    }
    if (!append && Cli.Get(Args, "on-duplicate") is not null)
    {
        JsonOut.Error("on-duplicate-requires-append", "--on-duplicate only applies with --append.", null, 2);
    }
    if (strictHeader && (!append || format != "csv"))
    {
        JsonOut.Error("strict-header-requires-csv-append", "--strict-header only applies with --append --format csv.", null, 2);
    }
    if (overwrite && append)
    {
        JsonOut.Error("overwrite-requires-plain", "--overwrite only applies without --append (append never destroys ledger rows).", null, 2);
    }
    if (!File.Exists(templatePath))
    {
        JsonOut.Error("template-not-found", $"Template not found at '{templatePath}'", null, 1);
    }

    var onDuplicate = LedgerIo.ParseDuplicatePolicy(Args);
    var sourceFile = Path.GetFileName(pdfPath);

    // Ledger pre-checks run BEFORE the engine: a duplicate skip must cost no extraction work,
    // and an unrecognized target must be refused before anything else happens.
    CsvLedger? csvLedger = null;
    JsonLedger? jsonLedger = null;
    if (append)
    {
        if (format == "csv") csvLedger = LedgerIo.ReadCsvLedger(outputPath!);
        else jsonLedger = LedgerIo.ReadJsonLedger(outputPath!);

        var alreadyRecorded = format == "csv"
            ? csvLedger!.ContainsSource(sourceFile)
            : jsonLedger!.ContainsSource(sourceFile);

        if (alreadyRecorded && onDuplicate == DuplicateSourcePolicy.Skip)
        {
            JsonOut.Write(new
            {
                status = "ok",
                path = outputPath,
                ledger = new
                {
                    action = "skipped-duplicate",
                    sourceFile,
                    rowsAdded = 0,
                    totalRows = format == "csv" ? csvLedger!.RowCount : jsonLedger!.Count,
                    totalSources = (format == "csv" ? csvLedger!.SourceFiles : jsonLedger!.SourceFiles).Count,
                },
            });
            Environment.Exit(0); // idempotent no-op is success; the engine never ran
        }
        if (alreadyRecorded && onDuplicate == DuplicateSourcePolicy.Fail)
        {
            JsonOut.Error("duplicate-source",
                $"'{sourceFile}' is already recorded in '{outputPath}'.",
                "Use --on-duplicate replace to refresh its rows, or the default skip to leave them.", 1);
        }
    }
    else if (!string.IsNullOrEmpty(outputPath) && !overwrite &&
             LedgerIo.IsRecognizedLedger(outputPath!, out var recorded))
    {
        JsonOut.Error("existing-ledger",
            $"'{outputPath}' is an existing Docuoria ledger ({recorded.Count} source document(s)) - refusing to flatten it with a plain export.",
            "Pass --append to add this PDF to the ledger, --overwrite to replace the file, or choose a different --output path.", 1);
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
            var completeness = ok.Completeness;
            if (append)
            {
                var payloadText = Encoding.UTF8.GetString(payload.Span);
                var mergeOptions = new LedgerMergeOptions { DuplicatePolicy = onDuplicate, StrictHeader = strictHeader };

                LedgerMergeResult merge;
                string rendered;
                int totalRows, totalSources;
                if (format == "csv")
                {
                    merge = csvLedger!.Merge(sourceFile, template.Identifier, payloadText, mergeOptions);
                    rendered = csvLedger.Render();
                    totalRows = csvLedger.RowCount;
                    totalSources = csvLedger.SourceFiles.Count;
                }
                else
                {
                    merge = jsonLedger!.Merge(sourceFile, template.Identifier, payloadText, mergeOptions);
                    rendered = jsonLedger.Render();
                    totalRows = jsonLedger.Count;
                    totalSources = jsonLedger.SourceFiles.Count;
                }

                if (!merge.Success)
                {
                    JsonOut.Error(merge.ErrorCode!,
                        $"Ledger merge failed for '{sourceFile}': {merge.ErrorDetail}",
                        "The ledger file was not modified.", 1);
                }

                LedgerIo.WriteAtomic(outputPath!, rendered);
                JsonOut.Write(new
                {
                    status = "ok",
                    path = outputPath,
                    completeness,
                    ledger = new
                    {
                        action = LedgerIo.ActionLabel(merge.Action),
                        sourceFile,
                        rowsAdded = merge.RowsAdded,
                        rowsRemoved = merge.RowsRemoved,
                        columnsAdded = merge.ColumnsAdded.Count > 0 ? merge.ColumnsAdded : null,
                        totalRows,
                        totalSources,
                    },
                });
            }
            else if (!string.IsNullOrEmpty(outputPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath!))!);
                await File.WriteAllBytesAsync(outputPath!, payload.ToArray());
                JsonOut.Write(new { status = "ok", path = outputPath, completeness });
            }
            else
            {
                var text = Encoding.UTF8.GetString(payload.Span);
                JsonOut.Write(new { status = "ok", format, output = text, completeness });
            }
            if (!completeness.IsComplete)
                Environment.Exit(2);
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
