using ClosedXML.Excel;
using Cerberus.Compliance;
using Cerberus.Scanning;

namespace Cerberus.Tests;

internal static class Fixture
{
    public static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    public static readonly Catalog Catalog = new(
        "test", "Test Catalog", "1.0", "desc", ["X"],
        ["Compliant", "Partially compliant", "Non-compliant", "Not applicable"],
        [
            new CatalogSection(null, "Tech",
            [
                new Control("C-1", "Crypto", "Use strong crypto.", null, true, ["crypto"]),
                new Control("C-2", "Secrets", "No hardcoded secrets.", null, true, ["secret"]),
                new Control("O-1", "Insurance", "Carry insurance.", null, false),
                new Control("O-2", "Audits", "Permit audits.", null, false),
            ]),
        ],
        new CatalogMethodology(new CorrectiveActions(30, 180)));

    /// <summary>C-1 non-compliant, C-2 compliant, O-1 and O-2 not assessed.</summary>
    public static IReadOnlyList<ControlResult> Controls()
    {
        var finding = new Finding("f1", "Weak hash", Severity.Medium, "crypto", "CWE-327", "src/a.py", 12, "md5(x)", "low");
        return new ComplianceEngine().Evaluate(Catalog, [finding]).Controls;
    }

    public static readonly IReadOnlyDictionary<string, OperatorResponse> NoResponses =
        new Dictionary<string, OperatorResponse>();
}

public class OperatorResponseValidationTests
{
    private static (OperatorResponse? Response, string? Error) Validate(
        string? verdict, string? justification = "Because.", string? plan = null, int? days = null) =>
        OperatorResponses.Validate("O-1", new OperatorResponseInput(verdict, justification, plan, days), Fixture.Catalog, Fixture.Now);

    [Theory]
    [InlineData("Compliant", "Compliant")]
    [InlineData("non-compliant", "Non-compliant")]
    [InlineData("NonCompliant", "Non-compliant")]
    [InlineData("  not applicable ", "Not applicable")]
    public void Verdict_is_normalized_to_the_catalog_wording(string input, string expected)
    {
        var (response, error) = Validate(input, plan: "Fix it.");

        Assert.Null(error);
        Assert.Equal(expected, response!.Verdict);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mostly fine")]
    public void Unknown_verdict_is_rejected(string? verdict)
    {
        var (response, error) = Validate(verdict);

        Assert.Null(response);
        Assert.Contains("Verdict must be one of", error);
    }

    [Fact]
    public void Justification_is_required()
    {
        Assert.Contains("justification", Validate("Compliant", justification: "  ").Error);
    }

    [Theory]
    [InlineData("Non-compliant")]
    [InlineData("Partially compliant")]
    public void Action_plan_is_required_when_not_fully_compliant(string verdict)
    {
        Assert.Contains("action plan", Validate(verdict).Error);
        Assert.Null(Validate(verdict, plan: "Roll out MFA.").Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(181)]
    public void Time_limit_outside_the_catalog_range_is_rejected(int days)
    {
        Assert.Contains("between 1 and 180", Validate("Non-compliant", plan: "Fix.", days: days).Error);
    }

    [Fact]
    public void Valid_answer_is_trimmed_and_stamped()
    {
        var (response, _) = Validate("Partially compliant", "  In progress. ", "  Finish rollout. ", 90);

        Assert.Equal(new OperatorResponse("O-1", "Partially compliant", "In progress.", "Finish rollout.", 90, Fixture.Now), response);
    }
}

public class EffectiveSummaryTests
{
    [Fact]
    public void Without_answers_the_summary_mirrors_the_automated_verdicts()
    {
        var summary = OperatorResponses.Summarize(Fixture.Controls(), Fixture.NoResponses);

        Assert.Equal(new EffectiveSummary(1, 0, 1, 0, 2, 0), summary);
    }

    [Fact]
    public void Operator_answers_fill_gaps_and_overrule_the_scanner()
    {
        var responses = new Dictionary<string, OperatorResponse>
        {
            // Overrules the scanner's Non-compliant (false positive).
            ["C-1"] = new("C-1", "Compliant", "MD5 is only used for cache keys.", null, null, Fixture.Now),
            ["O-1"] = new("O-1", "Partially compliant", "Policy being renewed.", "Renew.", 60, Fixture.Now),
        };

        var summary = OperatorResponses.Summarize(Fixture.Controls(), responses);

        Assert.Equal(new EffectiveSummary(2, 1, 0, 0, 1, 2), summary);
    }
}

public class WorkbookImportTests
{
    private static readonly AssessmentInfo Info = new("abcdef0123456789", "test", "src", null, Fixture.Now, 50);

    private static WorkbookImport RoundTrip(
        Action<IXLWorksheet> edit, IReadOnlyDictionary<string, OperatorResponse>? existing = null)
    {
        existing ??= Fixture.NoResponses;
        var controls = Fixture.Controls();
        var bytes = ComplianceWorkbook.Build(Info, Fixture.Catalog, controls, existing);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        edit(workbook.Worksheet("Controls"));
        var edited = new MemoryStream();
        workbook.SaveAs(edited);
        edited.Position = 0;

        return ComplianceWorkbook.ReadResponses(edited, Fixture.Catalog, controls, existing, Fixture.Now);
    }

    private static IXLRow Row(IXLWorksheet sheet, string controlId) =>
        sheet.RowsUsed().Single(r => r.Cell(1).GetString() == controlId);

    [Fact]
    public void Untouched_export_imports_nothing()
    {
        var result = RoundTrip(_ => { });

        Assert.Empty(result.Accepted);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Unchanged);   // the two rows the scanner pre-filled
    }

    [Fact]
    public void Completed_row_is_imported()
    {
        var result = RoundTrip(sheet =>
        {
            var row = Row(sheet, "O-1");
            row.Cell(5).Value = "Partially compliant";
            row.Cell(6).Value = "Policy being renewed.";
            row.Cell(7).Value = "Renew before expiry.";
            row.Cell(8).Value = 60;
        });

        var response = Assert.Single(result.Accepted);
        Assert.Equal(new OperatorResponse("O-1", "Partially compliant", "Policy being renewed.", "Renew before expiry.", 60, Fixture.Now), response);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Overruling_the_scanner_is_imported()
    {
        var result = RoundTrip(sheet =>
        {
            var row = Row(sheet, "C-1");
            row.Cell(5).Value = "Compliant";
            row.Cell(6).Value = "False positive: MD5 is only used for cache keys.";
            row.Cell(7).Clear();
            row.Cell(8).Clear();
        });

        Assert.Equal("Compliant", Assert.Single(result.Accepted).Verdict);
    }

    [Fact]
    public void Invalid_rows_are_reported_and_valid_rows_still_imported()
    {
        var result = RoundTrip(sheet =>
        {
            Row(sheet, "O-1").Cell(5).Value = "Non-compliant";          // no action plan
            Row(sheet, "O-1").Cell(6).Value = "We have none.";
            Row(sheet, "O-2").Cell(5).Value = "Compliant";
            Row(sheet, "O-2").Cell(6).Value = "Audit clause in every contract.";
            var extra = sheet.LastRowUsed()!.RowBelow();
            extra.Cell(1).Value = "X-99";
            extra.Cell(5).Value = "Compliant";
            extra.Cell(6).Value = "?";
        });

        Assert.Equal("O-2", Assert.Single(result.Accepted).ControlId);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.ControlId == "O-1" && e.Error.Contains("action plan"));
        Assert.Contains(result.Errors, e => e.ControlId == "X-99" && e.Error.Contains("not part of the assessment"));
    }

    [Fact]
    public void Stored_answers_are_exported_and_not_reimported_unless_edited()
    {
        var existing = new Dictionary<string, OperatorResponse>
        {
            ["O-1"] = new("O-1", "Compliant", "Certificate on file.", null, null, Fixture.Now.AddDays(-1)),
        };

        var untouched = RoundTrip(sheet =>
        {
            var row = Row(sheet, "O-1");
            Assert.Equal("Compliant", row.Cell(5).GetString());
            Assert.Equal("Certificate on file.", row.Cell(6).GetString());
            Assert.Equal("Operator", row.Cell(11).GetString());
        }, existing);
        Assert.Empty(untouched.Accepted);

        var edited = RoundTrip(sheet => Row(sheet, "O-1").Cell(6).Value = "Certificate renewed 2026.", existing);
        Assert.Equal("Certificate renewed 2026.", Assert.Single(edited.Accepted).Justification);
    }

    [Fact]
    public void Scanner_verdict_stays_visible_next_to_an_operator_answer()
    {
        var existing = new Dictionary<string, OperatorResponse>
        {
            ["C-1"] = new("C-1", "Compliant", "False positive.", null, null, Fixture.Now),
        };

        RoundTrip(sheet =>
        {
            var row = Row(sheet, "C-1");
            Assert.Equal("Compliant", row.Cell(5).GetString());
            Assert.Equal("Non-compliant", row.Cell(10).GetString());
        }, existing);
    }

    [Fact]
    public void A_file_that_is_not_a_cerberus_export_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => ComplianceWorkbook.ReadResponses(
            new MemoryStream([1, 2, 3, 4]), Fixture.Catalog, Fixture.Controls(), Fixture.NoResponses, Fixture.Now));

        using var other = new XLWorkbook();
        other.Worksheets.Add("Sheet1").Cell(1, 1).Value = "hello";
        var stream = new MemoryStream();
        other.SaveAs(stream);
        stream.Position = 0;

        var ex = Assert.Throws<InvalidDataException>(() => ComplianceWorkbook.ReadResponses(
            stream, Fixture.Catalog, Fixture.Controls(), Fixture.NoResponses, Fixture.Now));
        Assert.Contains("Controls", ex.Message);
    }
}
