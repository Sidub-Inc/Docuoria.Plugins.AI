#load "_common.csx"

#nullable enable

// F7 + recurring-export-ledgers — Batch execution: classify-route every PDF in a corpus and
// merge the extracted rows into consolidated ledger output. Routing is on the top-ranked
// classification's Recommendation: "strong" executes the matched template; "partial" and
// "no-match" are recorded as skipped — the batch never guesses (the calling agent resolves
// partials per classification.md and re-runs).
//
// Output is a LEDGER: CSV leads with sourceFile/templateId provenance columns then the union
// of per-template headers (first-seen order); JSON (--format json) is an array of
// { sourceFile, templateId, data } envelopes, one per PDF.
//
// --append extends an existing ledger idempotently: PDFs already recorded are handled per
// --on-duplicate (skip default — detected BEFORE classification, so monthly folder sweeps only
// pay for new files; replace refreshes in place; fail aborts). Without --append the output is a
// fresh snapshot — but an existing ledger recording sources NOT present in the corpus is
// refused (`would-drop-sources`) unless --overwrite, because those rows could not be rebuilt.
// All writes are atomic (temp file + replace).
//
// --output may contain {templateId} and/or {sourceFile} tokens to route one ledger per
// structure or per input file; every ledger behavior applies per resolved path.
//
// Args: --corpus <dir> --output <path> (--store-path <dir> | --store-url <url> [--store-key <key>])
//       [--format csv|json] [--append] [--on-duplicate skip|replace|fail] [--strict-header] [--overwrite]
// stdout: { pdfs: [ { pdf, templateId, recommendation, status, rows, action?, reason?, error?, completeness? } ],
//           summary: { pdfCount, succeeded, incomplete, skipped, duplicates, failed,
//                      rowsWritten, totalRows, outputPath?, outputs: [ { path, rowsWritten, totalRows, totalSources, columnsAdded? } ] } }
// Exit codes: 0 = every PDF "ok" or "duplicate" · 2 = ran but ≥1 skipped/incomplete/failed
//             (ledgers still written for the ok rows) · 1 = hard error (bad args, missing
//             corpus, no store, zero PDFs, unrecognized/unsafe output target).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Docuoria.Configuration;
using Docuoria.Contracts;
using Docuoria.Output.Csv;
using Docuoria.Output.Json;
using Docuoria.Output.Ledger;
using Docuoria.Results;

/// <summary>Per-PDF envelope entry. Null members are omitted by DocuoriaJsonOptions.</summary>
public sealed class BatchPdfEntry
{
    public string Pdf { get; set; } = string.Empty;
    public string? TemplateId { get; set; }
    public string? Recommendation { get; set; }
    public string Status { get; set; } = string.Empty;
    public int Rows { get; set; }
    public string? Action { get; set; }
    public string? Reason { get; set; }
    public string? Error { get; set; }
    public Completeness? Completeness { get; set; }
}

static string RecommendationLabel(ClassificationRecommendation r) => r switch
{
    ClassificationRecommendation.Strong => "strong",
    ClassificationRecommendation.Partial => "partial",
    _ => "no-match",
};

static string SanitizeToken(string value)
{
    if (string.IsNullOrEmpty(value)) return "unknown";
    var invalid = Path.GetInvalidFileNameChars();
    var sb = new StringBuilder(value.Length);
    foreach (var ch in value) sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '-' : ch);
    return sb.ToString();
}

try
{
    Cli.Help(Args, "batch-execute.csx", "Classify-route every PDF in a corpus and merge extracted rows into ledger output",
        ("corpus", true, "Directory containing the PDFs to process", false),
        ("output", true, "Ledger path to write; may contain {templateId} / {sourceFile} tokens", false),
        ("store-path", false, "Local template store directory (required unless --store-url is given)", false),
        ("store-url", false, "API template store URL (mutually exclusive with --store-path)", false),
        ("store-key", false, "Function key for API store authentication", false),
        ("format", false, "Ledger format: csv (default) or json", false),
        ("append", false, "Extend the existing ledger(s) idempotently instead of rebuilding", true),
        ("on-duplicate", false, "With --append: skip|replace|fail for PDFs already recorded (default: skip)", false),
        ("strict-header", false, "With --append + csv: fail a PDF instead of adding new columns to the header", true),
        ("overwrite", false, "Allow a plain (non-append) run to replace an unrecognized or unregenerable output file", true));

    var corpusDir = Cli.Require(Args, "corpus");
    var outputPath = Cli.Require(Args, "output");
    var format = (Cli.Get(Args, "format") ?? "csv").Trim().ToLowerInvariant();
    var append = Cli.Has(Args, "append");
    var overwrite = Cli.Has(Args, "overwrite");
    var strictHeader = Cli.Has(Args, "strict-header");

    if (format != "csv" && format != "json")
    {
        JsonOut.Error("bad-format", "expected --format csv|json", null, 2);
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

    var onDuplicate = LedgerIo.ParseDuplicatePolicy(Args);

    // Classification routes every PDF, so the store is REQUIRED — unlike other store
    // scripts there is no silent ./templates default here.
    if (string.IsNullOrWhiteSpace(Cli.Get(Args, "store-path")) &&
        string.IsNullOrWhiteSpace(Cli.Get(Args, "store-url")))
    {
        JsonOut.Error("no-store",
            "A template store is required: pass --store-path <dir> or --store-url <url>.",
            "batch-execute classifies each PDF against the stored templates to route it.", 1);
    }

    if (!Directory.Exists(corpusDir))
    {
        JsonOut.Error("corpus-not-found", $"Corpus directory not found at '{corpusDir}'", null, 1);
    }

    var pdfPaths = Directory.GetFiles(corpusDir, "*.pdf", SearchOption.TopDirectoryOnly)
        .Concat(Directory.GetFiles(corpusDir, "*.PDF", SearchOption.TopDirectoryOnly))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
        .ToList();

    if (pdfPaths.Count == 0)
    {
        JsonOut.Error("empty-corpus", $"No PDF files found in corpus directory '{corpusDir}'.", null, 1);
    }

    var corpusNames = new HashSet<string>(pdfPaths.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);

    var hasTemplateToken = outputPath.Contains("{templateId}", StringComparison.Ordinal);
    var hasSourceToken = outputPath.Contains("{sourceFile}", StringComparison.Ordinal);
    var tokenized = hasTemplateToken || hasSourceToken;

    string ResolvePath(string? templateId, string sourceFileBase) => outputPath
        .Replace("{templateId}", SanitizeToken(templateId ?? string.Empty), StringComparison.Ordinal)
        .Replace("{sourceFile}", SanitizeToken(sourceFileBase), StringComparison.Ordinal);

    // One in-memory ledger per resolved output path. Append mode starts from the parsed existing
    // file (a duplicate check must see what is recorded); plain mode starts empty and replaces.
    var csvLedgers = new Dictionary<string, CsvLedger>(StringComparer.OrdinalIgnoreCase);
    var jsonLedgers = new Dictionary<string, JsonLedger>(StringComparer.OrdinalIgnoreCase);
    var pathOrder = new List<string>();
    var pathRowsAdded = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var pathColumnsAdded = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    var pathMutated = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    void GuardPlainOverwrite(string path)
    {
        if (!File.Exists(path) || overwrite) return;
        if (LedgerIo.IsRecognizedLedger(path, out var recorded))
        {
            var missing = recorded.Where(s => !corpusNames.Contains(s)).ToList();
            if (missing.Count == 0) return; // every recorded source is in the corpus → regenerable snapshot
            JsonOut.Error("would-drop-sources",
                $"'{path}' is a ledger recording {recorded.Count} source document(s); {missing.Count} are not in this corpus, so rebuilding would lose their rows.",
                $"Not in corpus: {string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? ", …" : "")}. " +
                "Pass --append to extend the ledger instead, or --overwrite to force a rebuild.", 1);
        }
        else
        {
            string content;
            try { content = File.ReadAllText(path); } catch { content = "x"; }
            if (string.IsNullOrWhiteSpace(content)) return; // empty placeholder → safe to replace
            JsonOut.Error("existing-output",
                $"'{path}' exists and is not a Docuoria ledger - refusing to overwrite it.",
                "Pass --overwrite to replace it or point --output at a different path.", 1);
        }
    }

    void EnsureLedger(string path)
    {
        if (format == "csv" ? csvLedgers.ContainsKey(path) : jsonLedgers.ContainsKey(path)) return;
        if (append)
        {
            if (format == "csv") csvLedgers[path] = LedgerIo.ReadCsvLedger(path); // exits not-a-ledger on garbage
            else jsonLedgers[path] = LedgerIo.ReadJsonLedger(path);
        }
        else
        {
            GuardPlainOverwrite(path);
            if (format == "csv") csvLedgers[path] = CsvLedger.CreateEmpty();
            else jsonLedgers[path] = JsonLedger.CreateEmpty();
        }
        pathOrder.Add(path);
    }

    bool LedgerContains(string path, string sourceFile) => format == "csv"
        ? csvLedgers[path].ContainsSource(sourceFile)
        : jsonLedgers[path].ContainsSource(sourceFile);

    // The plain (token-free) output resolves once; guard / load it up front so a hard refusal
    // costs zero engine work.
    if (!tokenized) EnsureLedger(ResolvePath(null, string.Empty));

    // Fail-fast duplicate scan: with a pre-resolvable path, --on-duplicate fail must abort
    // before ANY extraction work. (With {templateId} the path is known only after classification;
    // the abort then happens at detection time — still before anything is written.)
    if (append && !hasTemplateToken && onDuplicate == DuplicateSourcePolicy.Fail)
    {
        var dups = new List<string>();
        foreach (var pdfPath in pdfPaths)
        {
            var fn = Path.GetFileName(pdfPath);
            var p = ResolvePath(null, Path.GetFileNameWithoutExtension(fn));
            EnsureLedger(p);
            if (LedgerContains(p, fn)) dups.Add(fn);
        }
        if (dups.Count > 0)
        {
            JsonOut.Error("duplicate-source",
                $"{dups.Count} PDF(s) are already recorded in the output ledger.",
                $"Already recorded: {string.Join(", ", dups.Take(8))}{(dups.Count > 8 ? ", …" : "")}. " +
                "Use --on-duplicate replace to refresh them or skip (default) to leave them.", 1);
        }
    }

    using var host = ScriptHost.CreateHost(Args.ToArray(), includeStore: true);
    var engine = ScriptHost.GetEngine(host);

    var entries = new List<BatchPdfEntry>();
    var mergeOptions = new LedgerMergeOptions { DuplicatePolicy = onDuplicate, StrictHeader = strictHeader };

    foreach (var pdfPath in pdfPaths)
    {
        var fileName = Path.GetFileName(pdfPath);
        var sourceBase = Path.GetFileNameWithoutExtension(fileName);
        var entry = new BatchPdfEntry { Pdf = fileName };
        entries.Add(entry);

        try
        {
            // Duplicate short-circuit BEFORE classification: a recorded PDF under skip costs
            // no engine work — this is what makes a monthly whole-folder sweep cheap.
            if (append && !hasTemplateToken)
            {
                var preResolved = ResolvePath(null, sourceBase);
                EnsureLedger(preResolved);
                if (LedgerContains(preResolved, fileName) && onDuplicate == DuplicateSourcePolicy.Skip)
                {
                    entry.Status = "duplicate";
                    entry.Reason = "already-in-output";
                    continue;
                }
            }

            using var pdf = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var ranked = await engine.ClassifyRankedAsync(pdf, 1);
            var top = ranked.Count > 0 ? ranked[0] : null;

            entry.TemplateId = top?.TemplateIdentifier;
            entry.Recommendation = top is null ? null : RecommendationLabel(top.Recommendation);

            if (top is null || top.Recommendation == ClassificationRecommendation.NoMatch)
            {
                entry.Status = "skipped";
                entry.Reason = "no-template-match";
                continue;
            }

            if (top.Recommendation == ClassificationRecommendation.Partial)
            {
                // The batch never guesses: a partial match needs the partial-match decision
                // (classification.md) before this PDF can be routed.
                entry.Status = "skipped";
                entry.Reason = "partial-match";
                continue;
            }

            var resolvedPath = ResolvePath(top.TemplateIdentifier, sourceBase);
            EnsureLedger(resolvedPath);

            // {templateId} outputs could not be duplicate-checked before classification.
            if (append && hasTemplateToken && LedgerContains(resolvedPath, fileName))
            {
                if (onDuplicate == DuplicateSourcePolicy.Skip)
                {
                    entry.Status = "duplicate";
                    entry.Reason = "already-in-output";
                    continue;
                }
                if (onDuplicate == DuplicateSourcePolicy.Fail)
                {
                    JsonOut.Error("duplicate-source",
                        $"'{fileName}' is already recorded in '{resolvedPath}'.",
                        "Use --on-duplicate replace to refresh it or skip (default) to leave it. Nothing was written.", 1);
                }
            }

            // Strong → execute (defaults: comma, UTF-8 no BOM, CRLF / compact JSON).
            pdf.Position = 0;
            ProcessingResult result = format == "csv"
                ? await engine.ExecuteTemplateAsync<CsvOutputGenerator, CsvGeneratorOptions>(
                    pdf, top.Template, new CsvGeneratorOptions())
                : await engine.ExecuteTemplateAsync<JsonOutputGenerator, JsonGeneratorOptions>(
                    pdf, top.Template, new JsonGeneratorOptions());

            switch (result)
            {
                case SucceededResult ok:
                {
                    var payloadText = Encoding.UTF8.GetString(ok.Output.Payload.Span);
                    var merge = format == "csv"
                        ? csvLedgers[resolvedPath].Merge(fileName, top.TemplateIdentifier, payloadText, mergeOptions)
                        : jsonLedgers[resolvedPath].Merge(fileName, top.TemplateIdentifier, payloadText, mergeOptions);

                    if (!merge.Success)
                    {
                        // Per-PDF resilience: a refused merge (e.g. strict-header) is recorded
                        // and the batch continues; the ledger is untouched for this PDF.
                        entry.Status = "failed";
                        entry.Error = $"ledger-merge {merge.ErrorCode}: {merge.ErrorDetail}";
                        break;
                    }

                    entry.Rows = merge.RowsAdded;
                    if (append) entry.Action = LedgerIo.ActionLabel(merge.Action);

                    pathRowsAdded[resolvedPath] = pathRowsAdded.GetValueOrDefault(resolvedPath) + merge.RowsAdded;
                    if (merge.ColumnsAdded.Count > 0 && append)
                    {
                        if (!pathColumnsAdded.TryGetValue(resolvedPath, out var cols))
                            pathColumnsAdded[resolvedPath] = cols = new List<string>();
                        cols.AddRange(merge.ColumnsAdded);
                    }
                    pathMutated[resolvedPath] = pathMutated.GetValueOrDefault(resolvedPath)
                        || merge.RowsAdded > 0 || merge.RowsRemoved > 0 || merge.ColumnsAdded.Count > 0;

                    if (ok.Completeness.IsComplete)
                    {
                        entry.Status = "ok";
                    }
                    else
                    {
                        // Rows are still merged; the exit code reflects the gap.
                        entry.Status = "incomplete";
                        entry.Completeness = ok.Completeness;
                    }
                    break;
                }

                case RejectedResult rej:
                    entry.Status = "failed";
                    entry.Error = $"Rejected ({rej.Reason}){(rej.Detail is not null ? $": {rej.Detail}" : "")}";
                    break;

                case FailedResult fail:
                    entry.Status = "failed";
                    entry.Error = fail.InnerDetail is not null
                        ? $"{fail.ErrorMessage} ({fail.InnerDetail})"
                        : fail.ErrorMessage;
                    break;

                default:
                    entry.Status = "failed";
                    entry.Error = $"Unknown result type '{result.GetType().Name}'.";
                    break;
            }
        }
        catch (Exception perPdfEx)
        {
            // One bad PDF must not abort the batch — record and continue.
            entry.Status = "failed";
            entry.Error = perPdfEx.Message;
        }
    }

    // Write phase — everything above was in-memory, so a refusal/crash earlier touched nothing.
    // The token-free output is always written (even header-only, matching the original contract);
    // token-resolved outputs are written only when something actually changed in them.
    var outputs = new List<object>();
    var rowsWrittenTotal = 0;
    var totalRowsAll = 0;
    foreach (var p in pathOrder)
    {
        string rendered;
        int totalRows, totalSources;
        if (format == "csv")
        {
            var l = csvLedgers[p];
            rendered = l.Render(); totalRows = l.RowCount; totalSources = l.SourceFiles.Count;
        }
        else
        {
            var l = jsonLedgers[p];
            rendered = l.Render(); totalRows = l.Count; totalSources = l.SourceFiles.Count;
        }

        var added = pathRowsAdded.GetValueOrDefault(p);
        if (tokenized && !pathMutated.GetValueOrDefault(p) && File.Exists(p))
        {
            // Nothing changed in this ledger this run; leave the file byte-identical.
        }
        else if (tokenized && !pathMutated.GetValueOrDefault(p) && !File.Exists(p))
        {
            // Never create an empty token-resolved ledger for a PDF that ended up skipped.
            continue;
        }
        else
        {
            LedgerIo.WriteAtomic(p, rendered);
        }

        var cols = pathColumnsAdded.TryGetValue(p, out var c) && c.Count > 0 ? c : null;
        outputs.Add(new
        {
            path = Path.GetFullPath(p),
            rowsWritten = added,
            totalRows,
            totalSources,
            columnsAdded = cols,
        });
        rowsWrittenTotal += added;
        totalRowsAll += totalRows;
    }

    var succeeded = entries.Count(e => e.Status == "ok");
    var incomplete = entries.Count(e => e.Status == "incomplete");
    var skipped = entries.Count(e => e.Status == "skipped");
    var duplicates = entries.Count(e => e.Status == "duplicate");
    var failed = entries.Count(e => e.Status == "failed");

    JsonOut.Write(new
    {
        pdfs = entries,
        summary = new
        {
            pdfCount = entries.Count,
            succeeded,
            incomplete,
            skipped,
            duplicates,
            failed,
            rowsWritten = rowsWrittenTotal,
            totalRows = totalRowsAll,
            outputPath = tokenized ? null : Path.GetFullPath(ResolvePath(null, string.Empty)),
            outputs,
        },
    });

    if (succeeded + duplicates != entries.Count)
    {
        // Partial batch: the ledgers hold the ok rows, but at least one PDF needs attention.
        // Duplicate skips are the append steady state, NOT a problem to surface via exit code.
        Environment.Exit(2);
    }
}
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
