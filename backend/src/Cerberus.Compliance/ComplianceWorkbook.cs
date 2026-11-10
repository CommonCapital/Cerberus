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
public static class ComplianceWorkbook
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const int MaxCellLength = 32_000;      // Excel's hard limit is 32,767
    private const int MaxEvidenceInResponse = 5;

    /// <param name="catalog">The catalog the assessment was made against; null if it is no longer installed.</param>
    public static byte[] Build(AssessmentInfo info, Catalog? catalog, IReadOnlyList<ControlResult> controls)
    {
        using var workbook = new XLWorkbook();
        AddSummary(workbook, info, catalog, controls);
        AddControls(workbook, catalog, controls);
        AddEvidence(workbook, controls);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public static string FileName(AssessmentInfo info) =>
        $"cerberus-{info.CatalogId}-{info.Id[..Math.Min(8, info.Id.Length)]}.xlsx";

    // --- Summary -------------------------------------------------------------

    private static void AddSummary(
        XLWorkbook workbook, AssessmentInfo info, Catalog? catalog, IReadOnlyList<ControlResult> controls)
    {
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

    private static void AddControls(XLWorkbook workbook, Catalog? catalog, IReadOnlyList<ControlResult> controls)
    {
        var sheet = workbook.Worksheets.Add("Controls");
        string[] headers =
        [
            "Req No", "Section", "Control", "Requirement",
            "Verdict", "Detailed Response", "Action Plan", "Time limit (days)", "Findings",
        ];
        WriteHeader(sheet, headers);

        var requirements = catalog?.Controls.ToDictionary(c => c.Id, c => c.Requirement, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>();
        var standardDays = catalog?.Methodology?.CorrectiveActions?.StandardDays;

        var row = 2;
        foreach (var control in controls)
        {
            sheet.Cell(row, 1).Value = control.ControlId;
            sheet.Cell(row, 2).Value = control.Section;
            sheet.Cell(row, 3).Value = control.Title;
            sheet.Cell(row, 4).Value = Clip(requirements.GetValueOrDefault(control.ControlId, string.Empty));
            sheet.Cell(row, 5).Value = VerdictLabel(control.Verdict);
            sheet.Cell(row, 6).Value = Clip(DetailedResponse(control));
            sheet.Cell(row, 9).Value = control.Evidence.Count;

            if (control.Verdict == ControlVerdict.NonCompliant)
            {
                sheet.Cell(row, 7).Value =
                    $"Remediate the {control.Evidence.Count} finding(s) listed for {control.ControlId} on the Evidence sheet, then re-scan to confirm.";
                // Catalog guidance for corrective actions, where the catalog defines one.
                if (standardDays is { } days) sheet.Cell(row, 8).Value = days;
                sheet.Cell(row, 5).Style.Fill.SetBackgroundColor(XLColor.FromHtml("#F8D7DA"));
            }
            else if (control.Verdict == ControlVerdict.Compliant)
            {
                sheet.Cell(row, 5).Style.Fill.SetBackgroundColor(XLColor.FromHtml("#D4EDDA"));
            }
            row++;
        }

        // Verdict is a pick-list from the catalog's own response scale.
        var scale = catalog?.ResponseScale is { Count: > 0 } s
            ? s
            : ["Compliant", "Partially compliant", "Non-compliant", "Not applicable"];
        if (row > 2)
        {
            var validation = sheet.Range(2, 5, row - 1, 5).CreateDataValidation();
            validation.List($"\"{string.Join(",", scale)}\"", true);
            validation.IgnoreBlanks = true;
        }

        double[] widths = [10, 26, 34, 70, 18, 70, 50, 16, 10];
        for (var i = 0; i < widths.Length; i++) sheet.Column(i + 1).Width = widths[i];
        sheet.Columns(3, 7).Style.Alignment.SetWrapText(true);
        sheet.Rows().Style.Alignment.SetVertical(XLAlignmentVerticalValues.Top);
        sheet.SheetView.FreezeRows(1);
        if (row > 2) sheet.Range(1, 1, row - 1, headers.Length).SetAutoFilter();
    }

    /// <summary>The catalog's response-scale wording; blank when the engine could not judge the control.</summary>
    private static string VerdictLabel(ControlVerdict verdict) => verdict switch
    {
        ControlVerdict.Compliant => "Compliant",
        ControlVerdict.NonCompliant => "Non-compliant",
        _ => string.Empty,
    };

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
