using System.Buffers.Binary;

namespace Cerberus.Scanning;

/// <summary>
/// Hardening checks for Mach-O executables and libraries (macOS, iOS), read
/// from their headers as <see cref="BinaryHardening"/> does for ELF and PE:
/// position independence, an executable stack, a code signature and the
/// hardened runtime it asks for, and the stack-protector symbol. A universal
/// ("fat") binary is read slice by slice; a protection one slice lacks is
/// reported once, naming the slices.
///
/// What it cannot say: whether the signature is valid or trusted (it is not
/// verified, only found and read), and — as for ELF — whether a binary with no
/// stack-protector symbol had any function that needed one.
/// </summary>
internal static class MachOHardening
{
    private const uint Magic32 = 0xFEEDFACE, Magic64 = 0xFEEDFACF;
    private const uint Fat = 0xCAFEBABE, Fat64 = 0xCAFEBABF;

    private const uint MhExecute = 2, MhDylib = 6, MhBundle = 8;
    private const uint MhPie = 0x200000, MhAllowStackExecution = 0x20000;
    private const uint LcCodeSignature = 0x1D;

    // The signature is a blob of big-endian structures, whatever the binary's own byte order.
    private const uint CsSuperBlob = 0xFADE0CC0, CsCodeDirectory = 0xFADE0C02;
    private const uint CsRuntime = 0x00010000;

    /// <summary>More slices than any universal binary has: beyond this the bytes are something else.</summary>
    private const uint MaxSlices = 16;

    /// <summary>True for the first bytes of a thin Mach-O in either byte order.</summary>
    public static bool IsThin(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4) return false;
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
        return magic is Magic32 or Magic64 || BinaryPrimitives.ReverseEndianness(magic) is Magic32 or Magic64;
    }

    /// <summary>
    /// True for a universal binary. Java class files share its first four bytes; there the next four
    /// are the class-file version (45 and up), here the number of slices, which is small.
    /// </summary>
    public static bool IsFat(ReadOnlySpan<byte> head)
    {
        if (head.Length < 8) return false;
        var magic = BinaryPrimitives.ReadUInt32BigEndian(head);
        var slices = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
        return magic is Fat or Fat64 && slices is > 0 and <= MaxSlices;
    }

    public static BinaryReport? Inspect(ReadOnlySpan<byte> d)
    {
        if (IsThin(d)) return Report(InspectThin(d) is { } thin ? [thin] : []);
        if (!IsFat(d)) return null;

        var is64 = BinaryPrimitives.ReadUInt32BigEndian(d) == Fat64;
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(d[4..]);
        var entry = is64 ? 32 : 20;
        if (8 + count * entry > d.Length) return null;

        var slices = new List<Slice>();
        for (var i = 0; i < count; i++)
        {
            var at = 8 + i * entry;
            ulong offset = is64 ? BinaryPrimitives.ReadUInt64BigEndian(d[(at + 8)..]) : BinaryPrimitives.ReadUInt32BigEndian(d[(at + 8)..]);
            ulong size = is64 ? BinaryPrimitives.ReadUInt64BigEndian(d[(at + 16)..]) : BinaryPrimitives.ReadUInt32BigEndian(d[(at + 12)..]);
            if (offset >= (ulong)d.Length || size > (ulong)d.Length - offset) continue;   // a slice that is not all there
            if (InspectThin(d.Slice((int)offset, (int)size)) is { } slice) slices.Add(slice);
        }
        return slices.Count == 0 ? null : Report(slices);
    }

    /// <param name="Architecture">"arm64", "x86_64", … as a person reads it.</param>
    /// <param name="Issues">Rule id and the issue, for this slice alone.</param>
    private sealed record Slice(string Architecture, IReadOnlyList<BinaryIssue> Issues);

    /// <summary>One issue per rule, the slices that have it named in its evidence.</summary>
    private static BinaryReport? Report(IReadOnlyList<Slice> slices)
    {
        if (slices.Count == 0) return null;
        var issues = new List<BinaryIssue>();
        foreach (var group in slices.SelectMany(s => s.Issues.Select(i => (Slice: s, Issue: i))).GroupBy(x => x.Issue.RuleId))
        {
            var first = group.First().Issue;
            var where = slices.Count == 1 ? "" : $" ({string.Join(", ", group.Select(x => x.Slice.Architecture))} of {slices.Count} slices)";
            issues.Add(first with { Evidence = first.Evidence + where });
        }
        return new BinaryReport("Mach-O", issues);
    }

    private static Slice? InspectThin(ReadOnlySpan<byte> d)
    {
        if (d.Length < 28) return null;
        var raw = BinaryPrimitives.ReadUInt32LittleEndian(d);
        var little = raw is Magic32 or Magic64;
        var magic = little ? raw : BinaryPrimitives.ReverseEndianness(raw);
        if (magic is not (Magic32 or Magic64)) return null;
        var is64 = magic == Magic64;

        uint U32(ReadOnlySpan<byte> s, int at) =>
            little ? BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(at, 4)) : BinaryPrimitives.ReadUInt32BigEndian(s.Slice(at, 4));

        var cpu = U32(d, 4);
        var fileType = U32(d, 12);
        if (fileType is not (MhExecute or MhDylib or MhBundle)) return null;   // object files, dSYM companions and the like
        var commands = U32(d, 16);
        var commandBytes = U32(d, 20);
        var flags = U32(d, 24);
        var headerSize = is64 ? 32 : 28;
        if (commandBytes > d.Length - headerSize || commands > commandBytes / 8) return null;

        // Load commands: only the code signature is looked for.
        (uint Offset, uint Size)? signature = null;
        var at = headerSize;
        var end = headerSize + (int)commandBytes;
        for (var i = 0; i < commands && at + 8 <= end; i++)
        {
            var command = U32(d, at);
            var size = (int)U32(d, at + 4);
            if (size < 8 || at + size > end) return null;
            if (command == LcCodeSignature && size >= 16) signature = (U32(d, at + 8), U32(d, at + 12));
            at += size;
        }

        var issues = new List<BinaryIssue>();
        if (fileType == MhExecute && (flags & MhPie) == 0)
        {
            issues.Add(new("binary.macho.no-pie", "Executable is not position-independent (no PIE)", Severity.Medium, "high",
                "The Mach-O header lacks MH_PIE: the program loads at a fixed address, so address-space layout randomisation does not cover its code.",
                "Link as a position-independent executable (the default with current toolchains; remove -no_pie)."));
        }
        if ((flags & MhAllowStackExecution) != 0)
        {
            issues.Add(new("binary.macho.exec-stack", "Stack is executable", Severity.High, "high",
                "The Mach-O header sets MH_ALLOW_STACK_EXECUTION.",
                "Remove -allow_stack_execute from the link."));
        }

        if (signature is null)
        {
            issues.Add(new("binary.macho.unsigned", "Not code-signed", Severity.Low, "high",
                "There is no LC_CODE_SIGNATURE load command: the binary carries no signature to tie it to its author, and none of the protections a signature asks for.",
                "Sign it (codesign --sign with a Developer ID) as part of the release build."));
        }
        else if (CodeDirectoryFlags(d, signature.Value.Offset, signature.Value.Size) is { } signingFlags && (signingFlags & CsRuntime) == 0)
        {
            issues.Add(new("binary.macho.no-hardened-runtime", "Signed without the hardened runtime", Severity.Low, "medium",
                "The code signature does not ask for the hardened runtime (CS_RUNTIME): code injection, unsigned libraries and debugger attachment are not refused by the system.",
                "Sign with --options runtime, adding only the entitlements the program needs."));
        }

        if (d.IndexOf("___stack_chk_"u8) < 0)
        {
            issues.Add(new("binary.macho.no-stack-protector", "No stack-protector symbol found", Severity.Low, "low",
                "The binary does not reference ___stack_chk_fail or ___stack_chk_guard. Either it was built without stack protection, or no function in it needed a canary.",
                "Build with -fstack-protector-strong."));
        }

        return new Slice(Architecture(cpu), issues);
    }

    /// <summary>The flags of the signature's code directory; null when the signature cannot be read.</summary>
    private static uint? CodeDirectoryFlags(ReadOnlySpan<byte> d, uint offset, uint size)
    {
        if (offset >= d.Length || size < 12 || size > d.Length - offset) return null;
        var blob = d.Slice((int)offset, (int)size);
        if (BinaryPrimitives.ReadUInt32BigEndian(blob) != CsSuperBlob) return null;

        var count = BinaryPrimitives.ReadUInt32BigEndian(blob[8..]);
        if (count > (blob.Length - 12) / 8) return null;
        for (var i = 0; i < count; i++)
        {
            var at = (int)BinaryPrimitives.ReadUInt32BigEndian(blob[(12 + i * 8 + 4)..]);
            if (at < 0 || at + 16 > blob.Length) continue;
            // The first code directory is the one the system reads its flags from.
            if (BinaryPrimitives.ReadUInt32BigEndian(blob[at..]) == CsCodeDirectory) return BinaryPrimitives.ReadUInt32BigEndian(blob[(at + 12)..]);
        }
        return null;
    }

    private static string Architecture(uint cpu) => cpu switch
    {
        0x0100000C => "arm64",
        0x0200000C => "arm64_32",
        12 => "arm",
        0x01000007 => "x86_64",
        7 => "i386",
        18 => "ppc",
        0x01000012 => "ppc64",
        _ => $"cpu {cpu:x}",
    };
}
