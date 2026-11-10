using ClosedXML.Excel;
using Cerberus.Compliance;
using Cerberus.Scanning;

namespace Cerberus.Tests;

public class ComplianceEngineTests
{
    private static readonly Catalog TestCatalog = new(
        "test", "Test Catalog", "1.0", "desc", ["X"],
        ["Compliant", "Partially compliant", "Non-compliant", "Not applicable"],
        [
            new CatalogSection(null, "Tech",
            [
                new Control("C-1", "Crypto", "Use strong crypto.", null, true, ["crypto"]),
                new Control("C-2", "Secrets", "No hardcoded secrets.", null, true, ["secret"]),
                new Control("C-3", "Dependencies", "Patch components.", null, true, ["dependency"]),
                new Control("C-4", "Unmapped technical", "No scanner signal.", null, true),
            ]),
            new CatalogSection(null, "Org",
            [
                new Control("O-1", "Insurance", "Carry insurance.", null, false),
            ]),
        ],
        new CatalogMethodology(new CorrectiveActions(30, 180)));

    private static Finding F(string category, Severity severity = Severity.Medium, string title = "t") =>
        new($"id-{category}-{title}", title, severity, category, "CWE-0", "src/a.py", 7, "evidence", "high");

    private static ControlResult Get(ComplianceReport r, string id) => r.Controls.Single(c => c.ControlId == id);

    [Fact]
    public void Mapped_control_with_matching_findings_is_non_compliant_with_evidence()
    {
        var report = new ComplianceEngine().Evaluate(TestCatalog, [F("crypto"), F("crypto", Severity.High, "worse")]);

        var c1 = Get(report, "C-1");
        Assert.Equal(ControlVerdict.NonCompliant, c1.Verdict);
        Assert.Equal(2, c1.Evidence.Count);
        Assert.Equal(Severity.High, c1.Evidence[0].Severity); // most severe first
    }

    [Fact]
    public void Mapped_control_without_findings_is_compliant()
    {
        var report = new ComplianceEngine().Evaluate(TestCatalog, [F("crypto")]);

        Assert.Equal(ControlVerdict.Compliant, Get(report, "C-2").Verdict);
        Assert.Empty(Get(report, "C-2").Evidence);
    }

    [Fact]
    public void Unmapped_and_organizational_controls_are_not_assessed()
    {
        var report = new ComplianceEngine().Evaluate(TestCatalog, [F("crypto"), F("secret")]);

        Assert.Equal(ControlVerdict.NotAssessed, Get(report, "C-4").Verdict);
        Assert.Equal(ControlVerdict.NotAssessed, Get(report, "O-1").Verdict);
    }

    [Fact]
    public void Dependency_control_is_not_assessed_when_the_dependency_check_did_not_complete()
    {
        var report = new ComplianceEngine().Evaluate(TestCatalog, [], dependencyCheckCompleted: false);

        Assert.Equal(ControlVerdict.NotAssessed, Get(report, "C-3").Verdict);
        // Other mapped controls are unaffected.
        Assert.Equal(ControlVerdict.Compliant, Get(report, "C-1").Verdict);
    }

    [Fact]
    public void Dependency_control_is_compliant_only_when_the_check_completed_clean()
    {
        var report = new ComplianceEngine().Evaluate(TestCatalog, [], dependencyCheckCompleted: true);

        Assert.Equal(ControlVerdict.Compliant, Get(report, "C-3").Verdict);
    }

    [Fact]
    public void Dependency_findings_make_the_control_non_compliant_even_if_the_check_was_incomplete()
    {
        var report = new ComplianceEngine().Evaluate(TestCatalog, [F("dependency")], dependencyCheckCompleted: false);

        Assert.Equal(ControlVerdict.NonCompliant, Get(report, "C-3").Verdict);
    }

    [Theory]
    [InlineData("test")]
    [InlineData("docs")]
    public void Findings_only_in_test_or_documentation_files_do_not_fail_a_control(string context)
    {
        var fixture = F("crypto") with { Context = context, Confidence = "low" };

        var c1 = Get(new ComplianceEngine().Evaluate(TestCatalog, [fixture, fixture]), "C-1");

        Assert.Equal(ControlVerdict.Compliant, c1.Verdict);
        Assert.Empty(c1.Evidence);
        Assert.Contains("2 further finding(s) in test or documentation files were not counted", c1.Rationale);
    }

    [Fact]
    public void Production_findings_fail_the_control_and_fixtures_are_mentioned_separately()
    {
        var fixture = F("crypto", title: "fixture") with { Context = "test" };

        var c1 = Get(new ComplianceEngine().Evaluate(TestCatalog, [F("crypto"), fixture]), "C-1");

        Assert.Equal(ControlVerdict.NonCompliant, c1.Verdict);
        Assert.Equal("t", Assert.Single(c1.Evidence).Title);
        Assert.Contains("1 further finding(s)", c1.Rationale);
    }

    [Fact]
    public void Suppressed_findings_never_count()
    {
        var suppressed = F("crypto") with { Suppressed = true };

        var c1 = Get(new ComplianceEngine().Evaluate(TestCatalog, [suppressed]), "C-1");

        Assert.Equal(ControlVerdict.Compliant, c1.Verdict);
        Assert.DoesNotContain("further finding", c1.Rationale);
    }

    [Fact]
    public void Score_covers_only_automatically_assessed_controls()
    {
        var report = new ComplianceEngine().Evaluate(TestCatalog, [F("crypto")]);

        Assert.Equal(5, report.TotalControls);
        Assert.Equal(3, report.Assessed);      // C-1, C-2, C-3
        Assert.Equal(2, report.Compliant);
        Assert.Equal(1, report.NonCompliant);
        Assert.Equal(2, report.NotAssessed);
        Assert.Equal(66.7, report.Score);
    }

    [Fact]
    public void Score_is_null_when_nothing_could_be_assessed()
    {
        var catalog = TestCatalog with { Sections = [TestCatalog.Sections[1]] };

        Assert.Null(new ComplianceEngine().Evaluate(catalog, []).Score);
    }
}

public class CatalogLoaderTests
{
    // Whatever the static rules emit, plus the dependency (SCA) pass.
    private static readonly HashSet<string> ScannerCategories =
        new(Rules.Categories.Append("dependency"), StringComparer.OrdinalIgnoreCase);

    private static CatalogLoader RepoCatalogs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "catalogs"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return new CatalogLoader(new CatalogOptions(Path.Combine(dir!.FullName, "catalogs")));
    }

    [Fact]
    public void Shipped_catalogs_load()
    {
        var loader = RepoCatalogs();

        var esap = loader.Get("enterprise-sap");
        Assert.NotNull(esap);
        Assert.Equal(39, esap!.ControlCount);
        Assert.Equal(30, esap.Methodology?.CorrectiveActions?.StandardDays);

        var owasp = loader.Get("owasp-top10");
        Assert.NotNull(owasp);
        Assert.Equal(10, owasp!.ControlCount);
    }

    [Fact]
    public void Shipped_catalogs_have_unique_ids_and_only_known_scanner_categories()
    {
        foreach (var catalog in RepoCatalogs().All)
        {
            var ids = catalog.Controls.Select(c => c.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            foreach (var control in catalog.Controls)
            {
                foreach (var category in control.ScannerCategories ?? [])
                {
                    Assert.True(ScannerCategories.Contains(category),
                        $"{catalog.Id}/{control.Id} maps to unknown scanner category '{category}'");
                }
            }
        }
    }

    [Fact]
    public void Missing_catalog_directory_yields_no_catalogs()
    {
        var loader = new CatalogLoader(new CatalogOptions(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))));

        Assert.Empty(loader.All);
    }
}

public class ComplianceWorkbookTests
{
    private static readonly Catalog TestCatalog = new(
        "test", "Test Catalog", "1.0", "desc", ["X"],
        ["Compliant", "Partially compliant", "Non-compliant", "Not applicable"],
        [
            new CatalogSection(null, "Tech",
            [
                new Control("C-1", "Crypto", "Use strong crypto.", null, true, ["crypto"]),
                new Control("C-2", "Secrets", "No hardcoded secrets.", null, true, ["secret"]),
                new Control("O-1", "Insurance", "Carry insurance.", null, false),
            ]),
        ],
        new CatalogMethodology(new CorrectiveActions(30, 180)));

    private static XLWorkbook Export(Catalog? catalog, out AssessmentInfo info)
    {
        // A finding whose text looks like a formula must stay plain text.
        var finding = new Finding("f1", "=HYPERLINK(\"http://x\")", Severity.High, "crypto", "CWE-327",
            "src/a.py", 12, "md5(x)", "low");
        var report = new ComplianceEngine().Evaluate(TestCatalog, [finding]);
        info = new AssessmentInfo("abcdef0123456789", "test", "src", null,
            new DateTime(2026, 11, 10, 9, 30, 0, DateTimeKind.Utc), report.Score);

        var bytes = ComplianceWorkbook.Build(info, catalog, report.Controls);
        return new XLWorkbook(new MemoryStream(bytes));
    }

    private static IXLRow RowFor(IXLWorksheet sheet, string controlId) =>
        sheet.RowsUsed().Single(r => r.Cell(1).GetString() == controlId);

    [Fact]
    public void Workbook_has_the_three_sheets_and_one_row_per_control()
    {
        using var wb = Export(TestCatalog, out _);

        Assert.Equal(["Summary", "Controls", "Evidence"], wb.Worksheets.Select(w => w.Name).ToArray());
        Assert.Equal(1 + 3, wb.Worksheet("Controls").RowsUsed().Count());
        Assert.Equal(1 + 1, wb.Worksheet("Evidence").RowsUsed().Count());
    }

    [Fact]
    public void Non_compliant_control_gets_verdict_action_plan_and_catalog_time_limit()
    {
        using var wb = Export(TestCatalog, out _);
        var row = RowFor(wb.Worksheet("Controls"), "C-1");

        Assert.Equal("Use strong crypto.", row.Cell(4).GetString());
        Assert.Equal("Non-compliant", row.Cell(5).GetString());
        Assert.Contains("src/a.py:12", row.Cell(6).GetString());
        Assert.NotEqual(string.Empty, row.Cell(7).GetString());
        Assert.Equal(30, row.Cell(8).GetValue<int>());
    }

    [Fact]
    public void Compliant_control_has_no_action_plan_or_time_limit()
    {
        using var wb = Export(TestCatalog, out _);
        var row = RowFor(wb.Worksheet("Controls"), "C-2");

        Assert.Equal("Compliant", row.Cell(5).GetString());
        Assert.True(row.Cell(7).IsEmpty());
        Assert.True(row.Cell(8).IsEmpty());
    }

    [Fact]
    public void Not_assessed_control_is_left_blank_for_the_operator()
    {
        using var wb = Export(TestCatalog, out _);
        var row = RowFor(wb.Worksheet("Controls"), "O-1");

        Assert.Equal(string.Empty, row.Cell(5).GetString());
        Assert.StartsWith("[To be completed]", row.Cell(6).GetString());
        Assert.True(row.Cell(7).IsEmpty());
        Assert.True(row.Cell(8).IsEmpty());
    }

    [Fact]
    public void Finding_text_that_looks_like_a_formula_is_stored_as_text()
    {
        using var wb = Export(TestCatalog, out _);
        var cell = wb.Worksheet("Evidence").Row(2).Cell(3);

        Assert.False(cell.HasFormula);
        Assert.Equal(XLDataType.Text, cell.DataType);
        Assert.StartsWith("=HYPERLINK", cell.GetString());
    }

    [Fact]
    public void Export_still_works_when_the_catalog_is_no_longer_installed()
    {
        using var wb = Export(null, out var info);
        var row = RowFor(wb.Worksheet("Controls"), "C-1");

        Assert.Equal(string.Empty, row.Cell(4).GetString());   // no requirement text available
        Assert.True(row.Cell(8).IsEmpty());                    // no catalog deadline guidance
        Assert.Equal("cerberus-test-abcdef01.xlsx", ComplianceWorkbook.FileName(info));
    }
}
