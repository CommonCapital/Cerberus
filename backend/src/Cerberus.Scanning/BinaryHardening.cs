using System.Buffers.Binary;

namespace Cerberus.Scanning;

/// <summary>One missing or weakened protection in a built artefact.</summary>
public sealed record BinaryIssue(
    string RuleId,
    string Title,
    Severity Severity,
    string Confidence,
    string Evidence,
    string Remediation);

/// <param name="Format">"ELF" or "PE".</param>
/// <param name="Issues">Protections that are missing; empty for a hardened binary.</param>
public sealed record BinaryReport(string Format, IReadOnlyList<BinaryIssue> Issues);

/// <summary>
/// Hardening checks for built artefacts found in a target (PLAN.md §23, B3):
/// reads ELF and PE headers in-process and reports protections the build left
/// out — what build files only promise. Static and passive (tier T0): nothing
/// is executed and no external tool is started.
///
/// What it reads: for ELF — the file type (PIE), the GNU_STACK and GNU_RELRO
/// program headers, the dynamic section's bind-now flags and the stack-protector
/// symbol; for PE — the DllCharacteristics flags (ASLR, DEP, CFG, high-entropy
/// ASLR) and whether relocations were stripped. Mach-O is not read yet.
///
/// What it cannot say: a flag in a header is the build's statement, not a
/// measurement of the running process; and a binary with no stack-protector
/// symbol may simply have no function that needs one (that finding is low
/// confidence for this reason).
/// </summary>
public static class BinaryHardening
{
    public const string Category = "binary";
    public const string Cwe = "CWE-693";      // Protection Mechanism Failure
    public const string Owasp = "A05";        // Security Misconfiguration

    /// <summary>Larger files are not read; they are counted as skipped.</summary>
    public const int MaxBinaryBytes = 128 * 1024 * 1024;

    /// <summary>True when the first bytes are an ELF or DOS/PE signature.</summary>
    public static bool LooksLikeBinary(ReadOnlySpan<byte> head) =>
        head.Length >= 4 &&
        ((head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F') ||
         (head[0] == (byte)'M' && head[1] == (byte)'Z'));

    /// <summary>
    /// Inspect a file's bytes. Returns null when it is not an executable or
    /// shared library this reader understands (including truncated or malformed
    /// headers) — never throws on hostile input.
    /// </summary>
    public static BinaryReport? Inspect(ReadOnlySpan<byte> data)
    {
        if (!LooksLikeBinary(data)) return null;
        try
        {
            return data[0] == 0x7F ? InspectElf(data) : InspectPe(data);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            return null;   // a header pointing outside the file
        }
    }

    // --- ELF -------------------------------------------------------------------

    private const ushort EtExec = 2, EtDyn = 3;
    private const uint PtDynamic = 2, PtGnuStack = 0x6474e551, PtGnuRelro = 0x6474e552;
    private const uint PfX = 1;
    private const long DtBindNow = 24, DtFlags = 30, DtFlags1 = 0x6ffffffb;
    private const ulong DfBindNow = 0x8, Df1Now = 0x1;

    private static BinaryReport? InspectElf(ReadOnlySpan<byte> d)
    {
        if (d.Length < 52) return null;
        var is64 = d[4] switch { 1 => false, 2 => true, _ => (bool?)null };
        var little = d[5] switch { 1 => true, 2 => false, _ => (bool?)null };
        if (is64 is null || little is null) return null;
        var r = new Reader(little.Value);

        var type = r.U16(d, 16);
        if (type != EtExec && type != EtDyn) return null;   // object files and core dumps are not checked

        ulong phoff; int phentsize, phnum;
        if (is64.Value)
        {
            if (d.Length < 64) return null;
            phoff = r.U64(d, 32); phentsize = r.U16(d, 54); phnum = r.U16(d, 56);
        }
        else
        {
            phoff = r.U32(d, 28); phentsize = r.U16(d, 42); phnum = r.U16(d, 44);
        }
        if (phnum == 0 || phentsize < (is64.Value ? 56 : 32)) return null;
        if (phoff > (ulong)d.Length || (ulong)phentsize * (ulong)phnum > (ulong)d.Length - phoff) return null;

        bool hasStack = false, execStack = false, hasRelro = false, hasDynamic = false;
        ulong dynOffset = 0, dynSize = 0;

        for (var i = 0; i < phnum; i++)
        {
            var p = (int)phoff + i * phentsize;
            var ptype = r.U32(d, p);
            var flags = is64.Value ? r.U32(d, p + 4) : r.U32(d, p + 24);
            switch (ptype)
            {
                case PtGnuStack: hasStack = true; execStack = (flags & PfX) != 0; break;
                case PtGnuRelro: hasRelro = true; break;
                case PtDynamic:
                    hasDynamic = true;
                    dynOffset = is64.Value ? r.U64(d, p + 8) : r.U32(d, p + 4);
                    dynSize = is64.Value ? r.U64(d, p + 32) : r.U32(d, p + 16);
                    break;
            }
        }

        var bindNow = hasDynamic && HasBindNow(d, r, is64.Value, dynOffset, dynSize);
        var issues = new List<BinaryIssue>();

        if (type == EtExec)
        {
            issues.Add(new("binary.elf.no-pie", "Executable is not position-independent (no PIE)", Severity.Medium, "high",
                "ELF type is ET_EXEC: the program loads at a fixed address, so address-space layout randomisation does not cover its code.",
                "Build and link as a position-independent executable (-fPIE -pie)."));
        }

        if (hasStack && execStack)
        {
            issues.Add(new("binary.elf.exec-stack", "Stack is executable", Severity.High, "high",
                "The GNU_STACK program header is marked executable (PF_X).",
                "Link with -z noexecstack and remove whatever requires an executable stack (often an assembly file without a .note.GNU-stack section)."));
        }
        else if (!hasStack)
        {
            issues.Add(new("binary.elf.exec-stack", "Stack may be executable (no GNU_STACK header)", Severity.Medium, "medium",
                "There is no GNU_STACK program header; on many platforms the loader then gives the process an executable stack.",
                "Link with -z noexecstack so the binary states a non-executable stack."));
        }

        // Without a dynamic section there is no GOT for RELRO to protect.
        if (hasDynamic && !hasRelro)
        {
            issues.Add(new("binary.elf.no-relro", "No RELRO: relocation tables stay writable", Severity.Medium, "high",
                "There is no GNU_RELRO program header, so the global offset table remains writable for the life of the process.",
                "Link with -z relro -z now."));
        }
        else if (hasDynamic && !bindNow)
        {
            issues.Add(new("binary.elf.partial-relro", "Partial RELRO: lazy binding leaves part of the GOT writable", Severity.Low, "high",
                "GNU_RELRO is present but the dynamic section does not request immediate binding (BIND_NOW).",
                "Link with -z now for full RELRO."));
        }

        if (d.IndexOf("__stack_chk_fail"u8) < 0)
        {
            issues.Add(new("binary.elf.no-stack-protector", "No stack-protector symbol found", Severity.Low, "low",
                "The binary does not reference __stack_chk_fail. Either it was built without stack protection, or no function in it needed a canary.",
                "Build with -fstack-protector-strong."));
        }

        return new BinaryReport("ELF", issues);
    }

    private static bool HasBindNow(ReadOnlySpan<byte> d, Reader r, bool is64, ulong offset, ulong size)
    {
        if (offset > (ulong)d.Length) return false;
        size = Math.Min(size, (ulong)d.Length - offset);
        var entry = is64 ? 16 : 8;
        for (var p = (int)offset; p + entry <= (int)(offset + size); p += entry)
        {
            var tag = is64 ? (long)r.U64(d, p) : (int)r.U32(d, p);
            var value = is64 ? r.U64(d, p + 8) : r.U32(d, p + 4);
            if (tag == 0) break;   // DT_NULL
            if (tag == DtBindNow) return true;
            if (tag == DtFlags && (value & DfBindNow) != 0) return true;
            if (tag == DtFlags1 && (value & Df1Now) != 0) return true;
        }
        return false;
    }

    // --- PE --------------------------------------------------------------------

    private const ushort RelocsStripped = 0x0001, FileDll = 0x2000;
    private const ushort HighEntropyVa = 0x0020, DynamicBase = 0x0040, NxCompat = 0x0100, GuardCf = 0x4000;

    private static BinaryReport? InspectPe(ReadOnlySpan<byte> d)
    {
        if (d.Length < 0x40) return null;
        var r = new Reader(littleEndian: true);
        var lfanew = r.U32(d, 0x3C);
        if (lfanew > (uint)d.Length - 24 || lfanew > int.MaxValue) return null;
        var pe = (int)lfanew;
        if (!(d[pe] == (byte)'P' && d[pe + 1] == (byte)'E' && d[pe + 2] == 0 && d[pe + 3] == 0)) return null;

        var optionalSize = r.U16(d, pe + 20);
        var characteristics = r.U16(d, pe + 22);
        var opt = pe + 24;
        if (optionalSize < 72 || opt + optionalSize > d.Length) return null;   // an object file has no optional header

        var magic = r.U16(d, opt);
        var is64 = magic switch { 0x10b => false, 0x20b => true, _ => (bool?)null };
        if (is64 is null) return null;

        var dll = r.U16(d, opt + 70);
        var isDll = (characteristics & FileDll) != 0;

        // A managed (.NET) image: control-flow guard is a native-code protection and does not apply.
        var managed = false;
        var countAt = opt + (is64.Value ? 108 : 92);
        var directories = countAt + 4;
        if (optionalSize >= directories - opt + 15 * 8 && r.U32(d, countAt) > 14)
        {
            managed = r.U32(d, directories + 14 * 8) != 0;
        }

        var issues = new List<BinaryIssue>();

        if ((dll & DynamicBase) == 0)
        {
            issues.Add(new("binary.pe.no-aslr", "Image does not opt in to ASLR", Severity.Medium, "high",
                "DllCharacteristics lacks DYNAMIC_BASE: the image loads at its preferred address every time.",
                "Link with /DYNAMICBASE."));
        }
        else if (!isDll && (characteristics & RelocsStripped) != 0)
        {
            issues.Add(new("binary.pe.no-aslr", "ASLR is requested but relocations were stripped", Severity.Medium, "high",
                "DYNAMIC_BASE is set, but the file header says relocations were removed, so the image cannot be moved.",
                "Do not link with /FIXED; keep the relocation table."));
        }
        else if (is64.Value && (dll & HighEntropyVa) == 0)
        {
            issues.Add(new("binary.pe.no-high-entropy-va", "64-bit image without high-entropy ASLR", Severity.Low, "high",
                "DllCharacteristics lacks HIGH_ENTROPY_VA: the 64-bit address space is randomised with 32-bit entropy.",
                "Link with /HIGHENTROPYVA."));
        }

        if ((dll & NxCompat) == 0)
        {
            issues.Add(new("binary.pe.no-dep", "Image is not marked compatible with DEP", Severity.High, "high",
                "DllCharacteristics lacks NX_COMPAT: data pages may be executable in this process.",
                "Link with /NXCOMPAT."));
        }

        if (!managed && (dll & GuardCf) == 0)
        {
            issues.Add(new("binary.pe.no-cfg", "Control Flow Guard is not enabled", Severity.Low, "medium",
                "DllCharacteristics lacks GUARD_CF: indirect calls are not checked against the set of valid targets.",
                "Compile and link with /guard:cf."));
        }

        return new BinaryReport("PE", issues);
    }

    /// <summary>Bounds-checked integer reads in the file's byte order.</summary>
    private readonly struct Reader(bool littleEndian)
    {
        public ushort U16(ReadOnlySpan<byte> d, int at) =>
            littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(at, 2)) : BinaryPrimitives.ReadUInt16BigEndian(d.Slice(at, 2));

        public uint U32(ReadOnlySpan<byte> d, int at) =>
            littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(at, 4)) : BinaryPrimitives.ReadUInt32BigEndian(d.Slice(at, 4));

        public ulong U64(ReadOnlySpan<byte> d, int at) =>
            littleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(at, 8)) : BinaryPrimitives.ReadUInt64BigEndian(d.Slice(at, 8));
    }
}
