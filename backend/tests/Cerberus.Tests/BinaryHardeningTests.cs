using System.Buffers.Binary;
using System.Text;
using Cerberus.Scanning;
using Cerberus.Triage;

namespace Cerberus.Tests;

/// <summary>
/// Hardening checks on built artefacts. The binaries are assembled here byte by
/// byte — only the headers the reader looks at — so no compiled file is kept in
/// the repository and each test states exactly which protection is missing.
/// </summary>
public class BinaryHardeningTests
{
    // --- builders ----------------------------------------------------------------

    private const ushort EtRel = 1, EtExec = 2, EtDyn = 3;

    /// <summary>A minimal ELF: header, program headers, a dynamic section, and optionally the canary symbol.</summary>
    private static byte[] Elf(
        ushort type = EtDyn, bool is64 = true, bool little = true,
        bool gnuStack = true, bool execStack = false, bool relro = true, bool bindNow = true,
        bool dynamic = true, bool canary = true)
    {
        var headerSize = is64 ? 64 : 52;
        var phSize = is64 ? 56 : 32;
        var programHeaders = new List<(uint Type, uint Flags, int Offset, int Size)>();

        var dynEntry = is64 ? 16 : 8;
        var dynBytes = dynamic ? (bindNow ? 2 : 1) * dynEntry : 0;   // [DT_FLAGS], DT_NULL

        var count = 1 + (dynamic ? 1 : 0) + (gnuStack ? 1 : 0) + (relro ? 1 : 0);
        var dynOffset = headerSize + count * phSize;

        programHeaders.Add((1, 5, 0, dynOffset));                                         // PT_LOAD r-x
        if (dynamic) programHeaders.Add((2, 6, dynOffset, dynBytes));                     // PT_DYNAMIC
        if (gnuStack) programHeaders.Add((0x6474e551, execStack ? 7u : 6u, 0, 0));        // PT_GNU_STACK
        if (relro) programHeaders.Add((0x6474e552, 4, 0, 0));                             // PT_GNU_RELRO

        var symbol = canary ? Encoding.ASCII.GetBytes("\0__stack_chk_fail\0") : [];
        var file = new byte[dynOffset + dynBytes + symbol.Length];
        var s = file.AsSpan();

        void U16(int at, ushort v) { if (little) BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at), v); else BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(at), v); }
        void U32(int at, uint v) { if (little) BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), v); else BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(at), v); }
        void U64(int at, ulong v) { if (little) BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(at), v); else BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(at), v); }

        s[0] = 0x7F; s[1] = (byte)'E'; s[2] = (byte)'L'; s[3] = (byte)'F';
        s[4] = (byte)(is64 ? 2 : 1);
        s[5] = (byte)(little ? 1 : 2);
        s[6] = 1;
        U16(16, type);
        if (is64) { U64(32, (ulong)headerSize); U16(54, (ushort)phSize); U16(56, (ushort)count); }
        else { U32(28, (uint)headerSize); U16(42, (ushort)phSize); U16(44, (ushort)count); }

        for (var i = 0; i < programHeaders.Count; i++)
        {
            var (ptype, flags, offset, size) = programHeaders[i];
            var p = headerSize + i * phSize;
            U32(p, ptype);
            if (is64) { U32(p + 4, flags); U64(p + 8, (ulong)offset); U64(p + 32, (ulong)size); }
            else { U32(p + 4, (uint)offset); U32(p + 16, (uint)size); U32(p + 24, flags); }
        }

        if (dynamic && bindNow)
        {
            if (is64) { U64(dynOffset, 30); U64(dynOffset + 8, 0x8); }      // DT_FLAGS = DF_BIND_NOW
            else { U32(dynOffset, 30); U32(dynOffset + 4, 0x8); }
        }
        symbol.CopyTo(file, dynOffset + dynBytes);
        return file;
    }

    private const ushort HighEntropyVa = 0x0020, DynamicBase = 0x0040, NxCompat = 0x0100, GuardCf = 0x4000;
    private const ushort Hardened = HighEntropyVa | DynamicBase | NxCompat | GuardCf;

    /// <summary>A minimal PE image: DOS stub, signature, file header and an optional header with data directories.</summary>
    private static byte[] Pe(ushort dllCharacteristics = Hardened, bool is64 = true, ushort characteristics = 0x0022, bool managed = false)
    {
        const int pe = 0x40;
        var optionalSize = is64 ? 240 : 224;
        var file = new byte[pe + 24 + optionalSize];
        void U16(int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at), v);
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), v);

        file[0] = (byte)'M'; file[1] = (byte)'Z';
        U32(0x3C, pe);
        file[pe] = (byte)'P'; file[pe + 1] = (byte)'E';
        U16(pe + 4, (ushort)(is64 ? 0x8664 : 0x014c));
        U16(pe + 20, (ushort)optionalSize);
        U16(pe + 22, characteristics);

        var opt = pe + 24;
        U16(opt, (ushort)(is64 ? 0x20b : 0x10b));
        U16(opt + 70, dllCharacteristics);
        var countAt = opt + (is64 ? 108 : 92);
        U32(countAt, 16);
        if (managed) U32(countAt + 4 + 14 * 8, 0x2000);   // CLR runtime header
        return file;
    }

    private static string[] Rules(byte[] binary, string name = "app")
    {
        using var sandbox = new ScanSandbox().Bytes(name, binary);
        return sandbox.Scan().Findings.Select(f => f.Id.Split(':')[0]).Order().ToArray();
    }

    // --- ELF -----------------------------------------------------------------------

    [Fact]
    public void A_hardened_elf_has_no_findings_and_is_counted_as_inspected()
    {
        using var sandbox = new ScanSandbox().Bytes("server", Elf());
        var result = sandbox.Scan();

        Assert.Empty(result.Findings);
        Assert.Equal(1, result.BinariesInspected);
        Assert.Equal(0, result.FilesSkipped);
        Assert.Equal(0, result.FilesScanned);
    }

    [Fact]
    public void An_executable_built_without_pie_is_reported()
    {
        using var sandbox = new ScanSandbox().Bytes("out/server", Elf(type: EtExec));
        var finding = Assert.Single(sandbox.Scan().Findings);

        Assert.Equal("binary.elf.no-pie:out/server:1", finding.Id);
        Assert.Equal("binary", finding.Category);
        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Equal("CWE-693", finding.Cwe);
        Assert.Equal("A05", finding.Owasp);
        Assert.StartsWith("ELF: ", finding.Evidence);
        Assert.Contains("-pie", finding.Remediation);
    }

    [Fact]
    public void An_executable_stack_is_high_severity()
    {
        using var sandbox = new ScanSandbox().Bytes("app", Elf(execStack: true));
        var finding = Assert.Single(sandbox.Scan().Findings);
        Assert.Equal("binary.elf.exec-stack", finding.Id.Split(':')[0]);
        Assert.Equal(Severity.High, finding.Severity);
    }

    [Fact]
    public void A_missing_stack_header_is_reported_with_less_certainty()
    {
        using var sandbox = new ScanSandbox().Bytes("app", Elf(gnuStack: false));
        var finding = Assert.Single(sandbox.Scan().Findings);
        Assert.Equal("binary.elf.exec-stack", finding.Id.Split(':')[0]);
        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Equal("medium", finding.Confidence);
    }

    [Fact]
    public void Relro_is_graded_none_partial_full()
    {
        Assert.Equal(["binary.elf.no-relro"], Rules(Elf(relro: false, bindNow: false)));
        Assert.Equal(["binary.elf.partial-relro"], Rules(Elf(relro: true, bindNow: false)));
        Assert.Empty(Rules(Elf(relro: true, bindNow: true)));
    }

    [Fact]
    public void A_static_binary_is_not_asked_for_relro()
    {
        // No dynamic section: there is no GOT for RELRO to protect.
        Assert.Empty(Rules(Elf(dynamic: false, relro: false, bindNow: false)));
    }

    [Fact]
    public void A_missing_stack_protector_is_low_severity_and_low_confidence()
    {
        using var sandbox = new ScanSandbox().Bytes("app", Elf(canary: false));
        var finding = Assert.Single(sandbox.Scan().Findings);
        Assert.Equal("binary.elf.no-stack-protector", finding.Id.Split(':')[0]);
        Assert.Equal(Severity.Low, finding.Severity);
        Assert.Equal("low", finding.Confidence);
    }

    [Fact]
    public void Thirty_two_bit_big_endian_elf_is_read_too()
    {
        var rules = Rules(Elf(type: EtExec, is64: false, little: false, execStack: true, relro: false, bindNow: false, canary: false));
        Assert.Equal(
            ["binary.elf.exec-stack", "binary.elf.no-pie", "binary.elf.no-relro", "binary.elf.no-stack-protector"],
            rules);
    }

    [Fact]
    public void An_object_file_is_not_checked()
    {
        // ET_REL: not something that is loaded and run as it is.
        using var sandbox = new ScanSandbox().Bytes("module.o", Elf(type: EtRel, relro: false, canary: false));
        var result = sandbox.Scan();
        Assert.Empty(result.Findings);
        Assert.Equal(0, result.BinariesInspected);
        Assert.Equal(1, result.FilesSkipped);
    }

    // --- PE ------------------------------------------------------------------------

    [Fact]
    public void A_hardened_pe_has_no_findings()
    {
        using var sandbox = new ScanSandbox().Bytes("app.exe", Pe());
        var result = sandbox.Scan();
        Assert.Empty(result.Findings);
        Assert.Equal(1, result.BinariesInspected);
    }

    [Fact]
    public void A_pe_with_no_protections_reports_aslr_dep_and_cfg()
    {
        using var sandbox = new ScanSandbox().Bytes("legacy.dll", Pe(dllCharacteristics: 0));
        var findings = sandbox.Scan().Findings;

        Assert.Equal(
            ["binary.pe.no-aslr", "binary.pe.no-cfg", "binary.pe.no-dep"],
            findings.Select(f => f.Id.Split(':')[0]).Order().ToArray());
        // Most severe first, as for every scan.
        Assert.Equal("binary.pe.no-dep", findings[0].Id.Split(':')[0]);
        Assert.Equal(Severity.High, findings[0].Severity);
        Assert.All(findings, f => Assert.StartsWith("PE: ", f.Evidence));
    }

    [Fact]
    public void A_64_bit_image_without_high_entropy_aslr_is_a_low_finding()
    {
        Assert.Equal(["binary.pe.no-high-entropy-va"], Rules(Pe(dllCharacteristics: DynamicBase | NxCompat | GuardCf), "app.exe"));
        // 32-bit images have no such flag to set.
        Assert.Empty(Rules(Pe(dllCharacteristics: DynamicBase | NxCompat | GuardCf, is64: false), "app.exe"));
    }

    [Fact]
    public void Aslr_with_stripped_relocations_is_reported()
    {
        using var sandbox = new ScanSandbox().Bytes("app.exe", Pe(characteristics: 0x0023));   // RELOCS_STRIPPED
        var finding = Assert.Single(sandbox.Scan().Findings);
        Assert.Equal("binary.pe.no-aslr", finding.Id.Split(':')[0]);
        Assert.Contains("relocations", finding.Evidence);
    }

    [Fact]
    public void A_managed_assembly_is_not_asked_for_control_flow_guard()
    {
        Assert.Empty(Rules(Pe(dllCharacteristics: HighEntropyVa | DynamicBase | NxCompat, managed: true), "Service.dll"));
        Assert.Equal(["binary.pe.no-cfg"], Rules(Pe(dllCharacteristics: HighEntropyVa | DynamicBase | NxCompat), "native.dll"));
    }

    // --- the scan around it -----------------------------------------------------------

    [Fact]
    public void Text_that_only_starts_like_a_binary_is_scanned_as_text()
    {
        using var sandbox = new ScanSandbox().File("notes.py", "MZ = hashlib.md5(data).hexdigest()\n");
        var result = sandbox.Scan();
        Assert.Equal(0, result.BinariesInspected);
        Assert.Equal(1, result.FilesScanned);
        Assert.Contains(result.Findings, f => f.Id.StartsWith("crypto.weak-hash:"));
    }

    [Fact]
    public void A_binary_excluded_by_the_ignore_file_is_not_inspected()
    {
        using var sandbox = new ScanSandbox()
            .File(".cerberusignore", "prebuilt/\n")
            .Bytes("prebuilt/tool", Elf(type: EtExec));
        var result = sandbox.Scan();
        Assert.Empty(result.Findings);
        Assert.Equal(0, result.BinariesInspected);
        Assert.Equal(1, result.FilesIgnored);
    }

    [Fact]
    public void A_binary_among_test_data_is_low_confidence()
    {
        using var sandbox = new ScanSandbox().Bytes("tests/fixtures/sample", Elf(type: EtExec));
        var finding = Assert.Single(sandbox.Scan().Findings);
        Assert.Equal("test", finding.Context);
        Assert.Equal("low", finding.Confidence);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(20)]
    [InlineData(60)]
    [InlineData(100)]
    public void A_truncated_binary_is_skipped_not_fatal(int length)
    {
        using var sandbox = new ScanSandbox()
            .Bytes("cut.so", Elf(type: EtExec)[..length])
            .Bytes("cut.exe", Pe(dllCharacteristics: 0)[..length]);
        var result = sandbox.Scan();
        Assert.Empty(result.Findings);
        Assert.Equal(0, result.BinariesInspected);
        Assert.Equal(2, result.FilesSkipped);
    }

    [Fact]
    public void Headers_that_point_outside_the_file_do_not_throw()
    {
        var elf = Elf();
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(32), ulong.MaxValue - 8);   // e_phoff
        Assert.Null(BinaryHardening.Inspect(elf));

        var elfDyn = Elf();
        BinaryPrimitives.WriteUInt64LittleEndian(elfDyn.AsSpan(64 + 56 + 8), ulong.MaxValue);   // PT_DYNAMIC p_offset
        Assert.NotNull(BinaryHardening.Inspect(elfDyn));

        var pe = Pe();
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x3C), 0xFFFFFFF0);   // e_lfanew
        Assert.Null(BinaryHardening.Inspect(pe));

        Assert.Null(BinaryHardening.Inspect([]));
        Assert.Null(BinaryHardening.Inspect("MZ"u8));
    }

    [Fact]
    public void Binary_findings_are_not_sent_to_triage()
    {
        using var sandbox = new ScanSandbox()
            .Bytes("app", Elf(type: EtExec))
            .File("app.py", "digest = hashlib.md5(data).hexdigest()\n");
        var findings = sandbox.Scan().Findings;
        Assert.Contains(findings, f => f.Category == "binary");

        var selected = FindingTriager.Select(findings, 50, includeTestsAndDocs: true);
        Assert.NotEmpty(selected);
        Assert.DoesNotContain(selected, f => f.Category == "binary");
    }
}
