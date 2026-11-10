using ClosedXML.Excel;
using Cerberus.Scanning;

namespace Cerberus.Compliance;

/// <summary>What an exported workbook needs to know about the assessment itself.</summary>
public sealed record AssessmentInfo(
    string Id,
    string CatalogId,
    string TargetPath,
    string? ScanId,
    DateTime CreatedAt,
    double? Score);

/// <summary>
/// Exports a compliance assessment as a vendor-assurance spreadsheet
/// (PLAN.md §10): one row per control with Verdict, Detailed Response,
/// Action Plan and Time limit — the response schema enterprises expect back.
/// Only what the engine actually established is pre-filled; controls it could
/// not judge are left blank for the operator to complete.
/// </summary>
/// <summary>A row of an imported workbook that was rejected, with the reason.</summary>
public sealed record ImportError(int Row, string ControlId, string Error);

/// <summary>The outcome of reading operator answers from a workbook.</summary>
public sealed record WorkbookImport(
    IReadOnlyList<OperatorResponse> Accepted,
    int Unchanged,
    IReadOnlyList<ImportError> Errors);

public static class ComplianceWorkbook
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const int MaxCellLength = 32_000;      // Excel's hard limit is 32,767
    private const int MaxEvidenceInResponse = 5;
    private const string ControlsSheet = "Controls";

    /// <param name="catalog">The catalog the assessment was made against; null if it is no longer installed.</param>
    /// <param name="responses">Operator answers by control id; they replace the pre-filled automated values.</param>
    public static byte[] Build(
        AssessmentInfo info, Catalog? catalog, IReadOnlyList<ControlResult> controls,
        IReadOnlyDictionary<string, OperatorResponse>? responses = null)
    {
        responses ??= new Dictionary<string, OperatorResponse>();
        using var workbook = new XLWorkbook();
        AddSummary(workbook, info, catalog, controls, responses);
        AddControls(workbook, catalog, controls, responses);
        AddEvidence(workbook, controls);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public static string FileName(AssessmentInfo info) =>
        $"cerberus-{info.CatalogId}-{info.Id[..Math.Min(8, info.Id.Length)]}.xlsx";

    // --- Summary -------------------------------------------------------------

    private static void AddSummary(
        XLWorkbook workbook, AssessmentInfo info, Catalog? catalog, IReadOnlyList<ControlResult> controls,
        IReadOnlyDictionary<string, OperatorResponse> responses)
    {
        var effective = OperatorResponses.Summarize(controls, responses);
        var sheet = workbook.Worksheets.Add("Summary");
        var compliant = controls.Count(c => c.Verdict == ControlVerdict.Compliant);
        var nonCompliant = controls.Count(c => c.Verdict == ControlVerdict.NonCompliant);
        var notAssessed = controls.Count(c => c.Verdict == ControlVerdict.NotAssessed);

        var rows = new (string Label, XLCellValue Value)[]
        {
            ("Catalog", catalog is null ? info.CatalogId : $"{catalog.Name} (v{catalog.Version})"),
            ("Target", info.TargetPath),
            ("Assessment id", info.Id),
            ("Source scan id", info.ScanId ?? "(fresh scan)"),
            ("Assessed at (UTC)", info.CreatedAt.ToString("yyyy-MM-dd HH:mm")),
            ("Total controls", controls.Count),
            ("Compliant (automated)", compliant),
            ("Non-compliant (automated)", nonCompliant),
            ("Not assessed — to be completed", notAssessed),
            ("Score of automatically assessed controls (%)", info.Score is { } s ? s : "n/a"),
            ("Answered by operator", effective.OperatorAnswered),
            ("Still unanswered", effective.Unanswered),
        };

        sheet.Cell(1, 1).Value = "Cerberus compliance assessment";
        sheet.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);

        var row = 3;
        foreach (var (label, value) in rows)
        {
            sheet.Cell(row, 1).Value = label;
            sheet.Cell(row, 1).Style.Font.SetBold();
            sheet.Cell(row, 2).Value = value;
            sheet.Cell(row, 2).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Left);
            row++;
        }

        row++;
        string[] notes =
        [
            "How to read this workbook",
            "• Verdicts are produced by a deterministic passive scan, not by an auditor.",
            "• \"Compliant\" means only that the scanner found nothing in the categories mapped to that control.",
            "• The score covers automatically assessed controls only; it is not an overall compliance grade.",
            "• Controls with an empty Verdict could not be judged automatically — complete Verdict, Detailed Response, Action Plan and Time limit by hand.",
            "• Edit the Controls sheet and import it back into Cerberus to record your answers; rows you leave untouched are ignored.",
            "• An operator answer replaces the scanner's verdict for that control; the scanner's own verdict stays visible in \"Automated verdict\".",
        ];
        foreach (var note in notes)
        {
            sheet.Cell(row, 1).Value = note;
            row++;
        }
        sheet.Cell(row - notes.Length, 1).Style.Font.SetBold();

        sheet.Column(1).Width = 46;
        sheet.Column(2).Width = 60;
    }

    // --- Controls (the vendor-assurance response sheet) ----------------------

    // Column headers of the Controls sheet; the importer locates columns by these names.
    private static readonly string[] ControlHeaders =
    [
        "Req No", "Section", "Control", "Requirement",
        "Verdict", "Detailed Response", "Action Plan", "Time limit (days)", "Findings",
        "Automated verdict", "Answered by",
    ];

    private static void AddControls(
        XLWorkbook workbook, Catalog? catalog, IReadOnlyList<ControlResult> controls,
        IReadOnlyDictionary<string, OperatorResponse> responses)
    {
        var sheet = workbook.Worksheets.Add(ControlsSheet);
        WriteHeader(sheet, ControlHeaders);

        var requirements = catalog?.Controls.ToDictionary(c => c.Id, c => c.Requirement, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>();

        var row = 2;
        foreach (var control in controls)
        {
            var hasResponse = responses.TryGetValue(control.ControlId, out var response);
            var cells = hasResponse
                ? new RowValues(response!.Verdict, response.Justification, response.ActionPlan ?? string.Empty, response.TimeLimitDays)
                : Prefill(control, catalog);

            sheet.Cell(row, 1).Value = control.ControlId;
            sheet.Cell(row, 2).Value = control.Section;
            sheet.Cell(row, 3).Value = control.Title;
            sheet.Cell(row, 4).Value = Clip(requirements.GetValueOrDefault(control.ControlId, string.Empty));
            sheet.Cell(row, 5).Value = cells.Verdict;
            sheet.Cell(row, 6).Value = Clip(cells.Response);
            if (cells.ActionPlan.Length > 0) sheet.Cell(row, 7).Value = Clip(cells.ActionPlan);
            if (cells.TimeLimitDays is { } days) sheet.Cell(row, 8).Value = days;
            sheet.Cell(row, 9).Value = control.Evidence.Count;
            sheet.Cell(row, 10).Value = OperatorResponses.AutomatedLabel(control.Verdict);
            sheet.Cell(row, 11).Value = hasResponse ? "Operator"
                : control.Verdict == ControlVerdict.NotAssessed ? string.Empty : "Scanner";

            var color = OperatorResponses.Normalize(cells.Verdict) switch
            {
                "compliant" => "#D4EDDA",
                "noncompliant" => "#F8D7DA",
                "partiallycompliant" => "#FFF3CD",
                _ => null,
            };
            if (color is not null) sheet.Cell(row, 5).Style.Fill.SetBackgroundColor(XLColor.FromHtml(color));
            row++;
        }

        // Verdict is a pick-list from the catalog's own response scale.
        if (row > 2)
        {
            var validation = sheet.Range(2, 5, row - 1, 5).CreateDataValidation();
            validation.List($"\"{string.Join(",", OperatorResponses.ScaleOf(catalog))}\"", true);
            validation.IgnoreBlanks = true;
        }

        double[] widths = [10, 26, 34, 70, 18, 70, 50, 16, 10, 18, 12];
        for (var i = 0; i < widths.Length; i++) sheet.Column(i + 1).Width = widths[i];
        sheet.Columns(3, 7).Style.Alignment.SetWrapText(true);
        sheet.Rows().Style.Alignment.SetVertical(XLAlignmentVerticalValues.Top);
        sheet.SheetView.FreezeRows(1);
        if (row > 2) sheet.Range(1, 1, row - 1, ControlHeaders.Length).SetAutoFilter();
    }

    /// <summary>The four editable cells of a control row.</summary>
    private sealed record RowValues(string Verdict, string Response, string ActionPlan, int? TimeLimitDays);

    /// <summary>What the export writes for a control the operator has not answered.</summary>
    private static RowValues Prefill(ControlResult control, Catalog? catalog)
    {
        if (control.Verdict != ControlVerdict.NonCompliant)
        {
            return new RowValues(OperatorResponses.AutomatedLabel(control.Verdict), DetailedResponse(control), string.Empty, null);
        }
        return new RowValues(
            OperatorResponses.AutomatedLabel(control.Verdict),
            DetailedResponse(control),
            $"Remediate the {control.Evidence.Count} finding(s) listed for {control.ControlId} on the Evidence sheet, then re-scan to confirm.",
            // Catalog guidance for corrective actions, where the catalog defines one.
            catalog?.Methodology?.CorrectiveActions?.StandardDays);
    }

    private static string DetailedResponse(ControlResult control)
    {
        if (control.Verdict == ControlVerdict.NotAssessed)
        {
            return $"[To be completed] {control.Rationale}";
        }
        if (control.Evidence.Count == 0)
        {
            return $"Automated passive scan: {control.Rationale}";
        }

        var lines = new List<string> { $"Automated passive scan: {control.Rationale}" };
        lines.AddRange(control.Evidence.Take(MaxEvidenceInResponse).Select(f =>
            $"• [{f.Severity}] {f.Title} — {f.File}:{f.Line}"));
        if (control.Evidence.Count > MaxEvidenceInResponse)
        {
            lines.Add($"… and {control.Evidence.Count - MaxEvidenceInResponse} more (see the Evidence sheet).");
        }
        return string.Join("\n", lines);
    }

    // --- Evidence ------------------------------------------------------------

    private static void AddEvidence(XLWorkbook workbook, IReadOnlyList<ControlResult> controls)
    {
        var sheet = workbook.Worksheets.Add("Evidence");
        string[] headers =
        [
            "Req No", "Severity", "Finding", "Category", "CWE", "File", "Line",
            "Package", "Version", "Fixed in", "Advisory", "Reference", "Evidence",
        ];
        WriteHeader(sheet, headers);

        var row = 2;
        foreach (var control in controls)
        {
            foreach (var f in control.Evidence)
            {
                sheet.Cell(row, 1).Value = control.ControlId;
                sheet.Cell(row, 2).Value = f.Severity.ToString();
                sheet.Cell(row, 3).Value = Clip(f.Title);
                sheet.Cell(row, 4).Value = f.Category;
                sheet.Cell(row, 5).Value = f.Cwe ?? string.Empty;
                sheet.Cell(row, 6).Value = f.File;
                sheet.Cell(row, 7).Value = f.Line;
                sheet.Cell(row, 8).Value = f.Package ?? string.Empty;
                sheet.Cell(row, 9).Value = f.Version ?? string.Empty;
                sheet.Cell(row, 10).Value = f.FixedVersion ?? string.Empty;
                sheet.Cell(row, 11).Value = f.VulnId ?? string.Empty;
                sheet.Cell(row, 12).Value = f.Reference ?? string.Empty;
                sheet.Cell(row, 13).Value = Clip(f.Evidence);
                row++;
            }
        }

        double[] widths = [10, 10, 50, 12, 12, 40, 8, 22, 12, 12, 20, 44, 80];
        for (var i = 0; i < widths.Length; i++) sheet.Column(i + 1).Width = widths[i];
        sheet.SheetView.FreezeRows(1);
        if (row > 2) sheet.Range(1, 1, row - 1, headers.Length).SetAutoFilter();
    }

    // --- Import (operator answers back in) -----------------------------------

    /// <summary>
    /// Reads operator answers from a completed export. Rows with an empty
    /// Verdict, rows still identical to what the export pre-filled, and rows
    /// identical to the stored answer are skipped; every other row is validated
    /// against the catalog's response schema.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a readable Cerberus export.</exception>
    public static WorkbookImport ReadResponses(
        Stream xlsx, Catalog? catalog, IReadOnlyList<ControlResult> controls,
        IReadOnlyDictionary<string, OperatorResponse> existing, DateTime now)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(xlsx);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException("The file is not a readable .xlsx workbook.", ex);
        }

        using (workbook)
        {
            if (!workbook.TryGetWorksheet(ControlsSheet, out var sheet))
            {
                throw new InvalidDataException($"The workbook has no \"{ControlsSheet}\" sheet.");
            }

            int Column(string header)
            {
                var cell = sheet.Row(1).CellsUsed()
                    .FirstOrDefault(c => string.Equals(c.GetString().Trim(), header, StringComparison.OrdinalIgnoreCase));
                return cell?.Address.ColumnNumber
                    ?? throw new InvalidDataException($"The \"{ControlsSheet}\" sheet has no \"{header}\" column.");
            }

            int idCol = Column(ControlHeaders[0]), verdictCol = Column(ControlHeaders[4]),
                responseCol = Column(ControlHeaders[5]), planCol = Column(ControlHeaders[6]),
                daysCol = Column(ControlHeaders[7]);

            var byId = controls.ToDictionary(c => c.ControlId, StringComparer.OrdinalIgnoreCase);
            var accepted = new List<OperatorResponse>();
            var errors = new List<ImportError>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unchanged = 0;

            foreach (var row in sheet.RowsUsed().Skip(1))
            {
                var rowNumber = row.RowNumber();
                var controlId = row.Cell(idCol).GetString().Trim();
                var verdict = row.Cell(verdictCol).GetString().Trim();
                if (controlId.Length == 0 || verdict.Length == 0) continue;   // nothing answered

                if (!byId.TryGetValue(controlId, out var control))
                {
                    errors.Add(new ImportError(rowNumber, controlId, "This control is not part of the assessment."));
                    continue;
                }
                if (!seen.Add(control.ControlId))
                {
                    errors.Add(new ImportError(rowNumber, control.ControlId, "Duplicate row for this control; only the first is used."));
                    continue;
                }

                var response = Text(row.Cell(responseCol));
                var plan = Text(row.Cell(planCol));
                var daysCell = row.Cell(daysCol);
                int? days = null;
                if (!daysCell.IsEmpty())
                {
                    if (!daysCell.TryGetValue<int>(out var parsed))
                    {
                        errors.Add(new ImportError(rowNumber, control.ControlId, "Time limit must be a whole number of days."));
                        continue;
                    }
                    days = parsed;
                }

                // Untouched since export: either the scanner's pre-fill or the stored answer.
                var current = existing.TryGetValue(control.ControlId, out var stored)
                    ? new RowValues(stored.Verdict, stored.Justification, stored.ActionPlan ?? string.Empty, stored.TimeLimitDays)
                    : Prefill(control, catalog);
                if (OperatorResponses.Normalize(verdict) == OperatorResponses.Normalize(current.Verdict) &&
                    response == Canonical(Clip(current.Response)) &&
                    plan == Canonical(Clip(current.ActionPlan)) &&
                    days == current.TimeLimitDays)
                {
                    unchanged++;
                    continue;
                }

                var (validated, error) = OperatorResponses.Validate(
                    control.ControlId, new OperatorResponseInput(verdict, response, plan, days), catalog, now);
                if (validated is null)
                {
                    errors.Add(new ImportError(rowNumber, control.ControlId, error!));
                    continue;
                }
                accepted.Add(validated);
            }

            return new WorkbookImport(accepted, unchanged, errors);
        }
    }

    private static string Text(IXLCell cell) => Canonical(cell.GetString());

    // Spreadsheet apps rewrite line endings; compare on a canonical form.
    private static string Canonical(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    // --- helpers -------------------------------------------------------------

    private static void WriteHeader(IXLWorksheet sheet, string[] headers)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            var cell = sheet.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.SetBold();
            cell.Style.Fill.SetBackgroundColor(XLColor.FromHtml("#E9ECEF"));
        }
    }

    private static string Clip(string value) =>
        value.Length <= MaxCellLength ? value : value[..MaxCellLength] + "…";
}
