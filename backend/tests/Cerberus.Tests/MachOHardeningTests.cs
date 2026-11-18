using System.Buffers.Binary;
using System.Text;
using Cerberus.Scanning;

namespace Cerberus.Tests;

/// <summary>
/// Hardening checks on Mach-O executables and libraries. The binaries are
/// assembled here byte by byte — a header, the load commands the reader looks
/// at, a code signature — so each test states exactly which protection is
/// missing and no compiled file is kept in the repository.
/// </summary>
public class MachOHardeningTests
{
    private const uint Execute = 2, Dylib = 6, Object = 1;
    private const uint Pie = 0x200000, StackExecution = 0x20000;
    private const uint Arm64 = 0x0100000C, X8664 = 0x01000007;
    private const uint Runtime = 0x00010000;

    /// <summary>
    /// A thin Mach-O. <paramref name="signature"/>: null for none, otherwise the code-directory flags
    /// of a signature that is written after the load commands.
    /// </summary>
    private static byte[] MachO(
        uint fileType = Execute, uint flags = Pie, uint? signature = Runtime, bool canary = true,
        bool is64 = true, bool little = true, uint cpu = Arm64)
    {
        var header = is64 ? 32 : 28;
        var commands = new List<byte[]>();

        // An ordinary command the reader steps over: LC_UUID, 24 bytes.
        commands.Add(Command(0x1B, 24, little));
        byte[] blob = [];
        if (signature is { } csFlags)
        {
            blob = SignatureBlob(csFlags);
            commands.Add(Command(0x1D, 16, little));   // LC_CODE_SIGNATURE; offset and size filled in below
        }

        var commandBytes = commands.Sum(c => c.Length);
        var symbol = canary ? Encoding.ASCII.GetBytes("\0___stack_chk_fail\0") : [];
        var signatureAt = header + commandBytes + symbol.Length;
        var file = new byte[signatureAt + blob.Length];

        void U32(int at, uint value)
        {
            if (little) BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), value);
            else BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(at), value);
        }

        U32(0, is64 ? 0xFEEDFACFu : 0xFEEDFACEu);
        U32(4, cpu);
        U32(12, fileType);
        U32(16, (uint)commands.Count);
        U32(20, (uint)commandBytes);
        U32(24, flags);

        var at = header;
        foreach (var command in commands)
        {
            command.CopyTo(file, at);
            if (BinaryPrimitives.ReadUInt32LittleEndian(command) == 0x1D || BinaryPrimitives.ReadUInt32BigEndian(command) == 0x1D)
            {
                U32(at + 8, (uint)signatureAt);
                U32(at + 12, (uint)blob.Length);
            }
            at += command.Length;
        }
        symbol.CopyTo(file, at);
        blob.CopyTo(file, signatureAt);
        return file;
    }

    private static byte[] Command(uint command, int size, bool little)
    {
        var bytes = new byte[size];
        if (little) { BinaryPrimitives.WriteUInt32LittleEndian(bytes, command); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)size); }
        else { BinaryPrimitives.WriteUInt32BigEndian(bytes, command); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)size); }
        return bytes;
    }

    /// <summary>A signature superblob with one code directory, big-endian as the format is.</summary>
    private static byte[] SignatureBlob(uint flags)
    {
        var blob = new byte[12 + 8 + 16];
        BinaryPrimitives.WriteUInt32BigEndian(blob, 0xFADE0CC0);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(4), (uint)blob.Length);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(8), 1);     // one blob
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(12), 0);    // slot: the code directory
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(16), 20);   // where it is
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(20), 0xFADE0C02);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(24), 16);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(28), 0x20400);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(32), flags);
        return blob;
    }

    /// <summary>A universal binary holding the given slices, each at an aligned offset.</summary>
    private static byte[] Universal(params (uint Cpu, byte[] Slice)[] slices)
    {
        var offset = 4096;
        var layout = slices.Select(s => { var at = offset; offset += (s.Slice.Length + 4095) / 4096 * 4096; return (s.Cpu, s.Slice, At: at); }).ToList();
        var file = new byte[offset];
        BinaryPrimitives.WriteUInt32BigEndian(file, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), (uint)slices.Length);
        for (var i = 0; i < layout.Count; i++)
        {
            var entry = 8 + i * 20;
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(entry), layout[i].Cpu);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(entry + 8), (uint)layout[i].At);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(entry + 12), (uint)layout[i].Slice.Length);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(entry + 16), 12);
            layout[i].Slice.CopyTo(file, layout[i].At);
        }
        return file;
    }

    private static string[] Rules(byte[] binary, string name = "app")
    {
        using var sandbox = new ScanSandbox().Bytes(name, binary);
        return sandbox.Scan().Findings.Select(f => f.Id.Split(':')[0]).Order().ToArray();
    }

    // --- thin binaries ------------------------------------------------------------

    [Fact]
    public void A_hardened_executable_has_no_findings_and_is_counted_as_inspected()
    {
        using var sandbox = new ScanSandbox().Bytes("Build/App", MachO());
        var result = sandbox.Scan();

        Assert.Empty(result.Findings);
        Assert.Equal(1, result.BinariesInspected);
        Assert.Equal(0, result.FilesSkipped);
    }

    [Fact]
    public void An_executable_without_pie_is_reported()
    {
        using var sandbox = new ScanSandbox().Bytes("Build/App", MachO(flags: 0));
        var finding = Assert.Single(sandbox.Scan().Findings);

        Assert.Equal("binary.macho.no-pie:Build/App:1", finding.Id);
        Assert.Equal("binary", finding.Category);
        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Equal("CWE-693", finding.Cwe);
        Assert.StartsWith("Mach-O: ", finding.Evidence);
    }

    [Fact]
    public void A_library_is_not_asked_for_pie()
    {
        // A dylib is loaded wherever the loader puts it: MH_PIE is an executable's flag.
        Assert.Empty(Rules(MachO(fileType: Dylib, flags: 0), "libthing.dylib"));
    }

    [Fact]
    public void An_executable_stack_is_high_severity()
    {
        using var sandbox = new ScanSandbox().Bytes("app", MachO(flags: Pie | StackExecution));
        var finding = Assert.Single(sandbox.Scan().Findings);
        Assert.Equal("binary.macho.exec-stack", finding.Id.Split(':')[0]);
        Assert.Equal(Severity.High, finding.Severity);
    }

    [Fact]
    public void An_unsigned_binary_and_one_signed_without_the_hardened_runtime_are_told_apart()
    {
        Assert.Equal(["binary.macho.unsigned"], Rules(MachO(signature: null)));
        Assert.Equal(["binary.macho.no-hardened-runtime"], Rules(MachO(signature: 0x2)));   // ad-hoc, no runtime
        Assert.Empty(Rules(MachO(signature: 0x2 | Runtime)));
    }

    [Fact]
    public void A_signature_that_cannot_be_read_is_not_called_weak()
    {
        // The load command points past the end of the file: unread, so no claim about its flags.
        var binary = MachO();
        BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(32 + 24 + 8), (uint)binary.Length + 100);
        Assert.Empty(Rules(binary));
    }

    [Fact]
    public void A_missing_stack_protector_is_low_severity_and_low_confidence()
    {
        using var sandbox = new ScanSandbox().Bytes("app", MachO(canary: false));
        var finding = Assert.Single(sandbox.Scan().Findings);
        Assert.Equal("binary.macho.no-stack-protector", finding.Id.Split(':')[0]);
        Assert.Equal(("low", Severity.Low), (finding.Confidence, finding.Severity));
    }

    [Fact]
    public void Thirty_two_bit_and_big_endian_binaries_are_read_too()
    {
        Assert.Equal(["binary.macho.no-pie", "binary.macho.unsigned"], Rules(MachO(is64: false, flags: 0, signature: null)));
        Assert.Equal(["binary.macho.exec-stack"], Rules(MachO(little: false, flags: Pie | StackExecution, cpu: 18)));
    }

    [Fact]
    public void An_object_file_is_not_checked()
    {
        using var sandbox = new ScanSandbox().Bytes("module.o", MachO(fileType: Object, flags: 0, signature: null, canary: false));
        var result = sandbox.Scan();
        Assert.Empty(result.Findings);
        Assert.Equal(0, result.BinariesInspected);
    }

    // --- universal binaries ------------------------------------------------------------

    [Fact]
    public void A_universal_binary_is_read_slice_by_slice_and_names_the_slices_that_fall_short()
    {
        var binary = Universal((Arm64, MachO(cpu: Arm64)), (X8664, MachO(cpu: X8664, flags: 0, signature: null)));
        using var sandbox = new ScanSandbox().Bytes("Payload/App", binary);
        var result = sandbox.Scan();

        Assert.Equal(1, result.BinariesInspected);
        Assert.Equal(["binary.macho.no-pie", "binary.macho.unsigned"], result.Findings.Select(f => f.Id.Split(':')[0]).Order().ToArray());
        Assert.All(result.Findings, f => Assert.EndsWith("(x86_64 of 2 slices)", f.Evidence));
    }

    [Fact]
    public void A_weakness_both_slices_share_is_reported_once()
    {
        var binary = Universal((Arm64, MachO(cpu: Arm64, signature: null)), (X8664, MachO(cpu: X8664, signature: null)));
        var finding = Assert.Single(new ScanSandbox().Bytes("App", binary).Scan().Findings);
        Assert.Equal("binary.macho.unsigned", finding.Id.Split(':')[0]);
        Assert.EndsWith("(arm64, x86_64 of 2 slices)", finding.Evidence);
    }

    [Fact]
    public void A_java_class_file_is_not_taken_for_a_universal_binary()
    {
        // The same first four bytes; then a class-file version (here 65, Java 21) where a universal binary has its slice count.
        byte[] classFile = [0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x41, 0x00, 0x0A, 0x01, 0x02];
        using var sandbox = new ScanSandbox().Bytes("Main.class", classFile);
        var result = sandbox.Scan();
        Assert.Empty(result.Findings);
        Assert.Equal(0, result.BinariesInspected);
        Assert.Null(BinaryHardening.Inspect(classFile));
    }

    // --- hostile input ----------------------------------------------------------------

    [Theory]
    [InlineData(4)]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(60)]
    public void A_truncated_binary_is_skipped_not_fatal(int length)
    {
        using var sandbox = new ScanSandbox()
            .Bytes("cut-thin", MachO(flags: 0)[..length])
            .Bytes("cut-fat", Universal((Arm64, MachO(flags: 0)))[..length]);
        var result = sandbox.Scan();
        Assert.Empty(result.Findings);
        Assert.Equal(0, result.BinariesInspected);
    }

    [Fact]
    public void Headers_that_point_outside_the_file_do_not_throw()
    {
        var commands = MachO();
        BinaryPrimitives.WriteUInt32LittleEndian(commands.AsSpan(20), uint.MaxValue);   // sizeofcmds
        Assert.Null(BinaryHardening.Inspect(commands));

        var count = MachO();
        BinaryPrimitives.WriteUInt32LittleEndian(count.AsSpan(16), 100_000);   // ncmds far beyond what fits
        Assert.Null(BinaryHardening.Inspect(count));

        var looping = MachO();
        BinaryPrimitives.WriteUInt32LittleEndian(looping.AsSpan(32 + 4), 0);   // a command of size 0 would never end
        Assert.Null(BinaryHardening.Inspect(looping));

        var slice = Universal((Arm64, MachO()));
        BinaryPrimitives.WriteUInt32BigEndian(slice.AsSpan(8 + 8), uint.MaxValue - 4);   // slice offset
        Assert.Null(BinaryHardening.Inspect(slice));

        var many = Universal((Arm64, MachO()));
        BinaryPrimitives.WriteUInt32BigEndian(many.AsSpan(4), 16);   // claims more slices than the header holds
        Assert.Null(BinaryHardening.Inspect(many.AsSpan(0, 64)));
    }
}
