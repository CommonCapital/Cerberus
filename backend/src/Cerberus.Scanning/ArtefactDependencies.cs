using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cerberus.Scanning;

/// <summary>
/// Dependency inventory from built artefacts (PLAN.md §23, B3): what actually
/// shipped, read from the artefact itself rather than from a manifest that may
/// be missing, out of date, or never have declared a vendored library. The
/// result feeds the same OSV check as the manifests.
///
/// - .NET: <c>*.deps.json</c> beside a published application (NuGet packages).
/// - Go: the module list and toolchain version the linker embeds in every
///   binary (Go modules, and the standard library as <c>stdlib</c>).
/// - Java: <c>pom.properties</c> inside a JAR / WAR / EAR, and inside the JARs
///   nested in it one level down, as in a Spring Boot or WAR layout (Maven).
///
/// Static and in-process: an archive is listed and a few small entries are
/// read; nothing is unpacked to disk or executed. What it cannot see: a
/// library compiled in without its metadata (a shaded JAR that dropped
/// <c>META-INF/maven</c>, a stripped C library, a JavaScript bundle).
/// </summary>
public static partial class ArtefactDependencies
{
    /// <summary>Larger binaries and archives are not read.</summary>
    public const int MaxArtefactBytes = 128 * 1024 * 1024;

    private const int MaxNestedArchiveBytes = 32 * 1024 * 1024;
    private const int MaxNestedArchives = 500;
    private const int MaxPropertiesBytes = 64 * 1024;

    public static bool IsDepsJson(string fileName) =>
        fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase);

    public static bool IsJavaArchive(string fileName) =>
        fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".war", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".ear", StringComparison.OrdinalIgnoreCase);

    /// <summary>ELF, PE or Mach-O by its first bytes: the formats a Go binary comes in.</summary>
    public static bool LooksLikeExecutable(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4) return false;
        if (BinaryHardening.LooksLikeBinary(head)) return true;
        var magic = (uint)(head[0] << 24 | head[1] << 16 | head[2] << 8 | head[3]);
        return magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE;
    }

    // --- .NET: *.deps.json -----------------------------------------------------------

    /// <summary>The NuGet packages a published .NET application was built with.</summary>
    public static void ParseDepsJson(string text, string file, List<Dependency> deps)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); } catch (JsonException) { return; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("libraries", out var libraries) ||
                libraries.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var library in libraries.EnumerateObject())
            {
                // Only packages: "project" is the application's own code, the rest are reference or runtime packs.
                if (library.Value.ValueKind != JsonValueKind.Object ||
                    !library.Value.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.String ||
                    !string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var slash = library.Name.LastIndexOf('/');
                if (slash <= 0 || slash == library.Name.Length - 1) continue;
                Add(deps, "NuGet", library.Name[..slash], library.Name[(slash + 1)..], file);
            }
        }
    }

    // --- Go: build information embedded by the linker ------------------------------------

    // The module list sits between two fixed 16-byte markers (runtime/debug, modinfo).
    private static ReadOnlySpan<byte> GoModulesStart => [0x30, 0x77, 0xaf, 0x0c, 0x92, 0x74, 0x08, 0x02, 0x41, 0xe1, 0xc1, 0x07, 0xe6, 0xd6, 0x18, 0xe6];
    private static ReadOnlySpan<byte> GoModulesEnd => [0xf9, 0x32, 0x43, 0x31, 0x86, 0x18, 0x20, 0x72, 0x00, 0x82, 0x42, 0x10, 0x41, 0x16, 0xd8, 0xf2];
    // "\xff Go buildinf:" — the first byte is 0xFF itself, not its UTF-8 encoding.
    private static ReadOnlySpan<byte> GoBuildInfo =>
        [0xff, (byte)' ', (byte)'G', (byte)'o', (byte)' ', (byte)'b', (byte)'u', (byte)'i', (byte)'l', (byte)'d', (byte)'i', (byte)'n', (byte)'f', (byte)':'];

    private const int MaxGoModuleListBytes = 4 * 1024 * 1024;

    [GeneratedRegex(@"^go(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex GoToolchain();

    /// <summary>
    /// The modules a Go binary was linked from, and the toolchain it was built
    /// with (reported as the <c>stdlib</c> package). Adds nothing for a binary
    /// that is not Go or was built without module information.
    /// </summary>
    public static void ParseGoBinary(ReadOnlySpan<byte> data, string file, List<Dependency> deps)
    {
        // Toolchain: after the 32-byte build-info header, a length-prefixed version string (Go 1.18 and later).
        var info = data.IndexOf(GoBuildInfo);
        if (info >= 0 && info + 33 < data.Length && (data[info + 15] & 0x2) != 0)
        {
            var length = data[info + 32];   // one varint byte is enough for a version string
            if (length is > 0 and < 0x80 && info + 33 + length <= data.Length)
            {
                var version = Encoding.ASCII.GetString(data.Slice(info + 33, length));
                var match = GoToolchain().Match(version);
                if (match.Success) Add(deps, "Go", "stdlib", match.Groups[1].Value, file);
            }
        }

        var start = data.IndexOf(GoModulesStart);
        if (start < 0) return;
        var body = data[(start + GoModulesStart.Length)..];
        var end = body.IndexOf(GoModulesEnd);
        if (end < 0 || end > MaxGoModuleListBytes) return;

        var first = deps.Count;
        foreach (var line in Encoding.UTF8.GetString(body[..end]).Split('\n'))
        {
            var fields = line.Split('\t');
            if (fields.Length < 3) continue;
            var (path, version) = (fields[1], fields[2]);
            if (!IsGoModule(path, version)) continue;

            if (fields[0] == "dep")
            {
                Add(deps, "Go", path, version.TrimStart('v'), file);
            }
            else if (fields[0] == "=>" && deps.Count > first)
            {
                // A replace directive: what was linked is the replacement, not the module named above it.
                deps[^1] = deps[^1] with { Name = path, Version = version.TrimStart('v') };
            }
        }
    }

    private static bool IsGoModule(string path, string version) =>
        path.Length is > 0 and <= 300 && version.Length is > 1 and <= 100 && version[0] == 'v' && char.IsAsciiDigit(version[1]) &&
        !path.Any(c => char.IsControl(c) || c == ' ');

    // --- Java: pom.properties inside an archive -------------------------------------------

    /// <summary>
    /// The Maven artefacts recorded inside a JAR / WAR / EAR and inside the
    /// archives nested in it one level down. A damaged archive adds nothing.
    /// </summary>
    public static void ParseJavaArchive(Stream stream, string file, List<Dependency> deps)
    {
        var nested = 0;
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in archive.Entries)
            {
                if (IsPomProperties(entry))
                {
                    ReadPomProperties(entry, file, deps);
                }
                else if (IsJavaArchive(entry.Name) && entry.Length is > 0 and <= MaxNestedArchiveBytes && nested < MaxNestedArchives)
                {
                    nested++;
                    using var inner = new MemoryStream();
                    using (var source = entry.Open()) CopyAtMost(source, inner, MaxNestedArchiveBytes);
                    inner.Position = 0;
                    try
                    {
                        using var innerArchive = new ZipArchive(inner, ZipArchiveMode.Read, leaveOpen: true);
                        foreach (var innerEntry in innerArchive.Entries)
                        {
                            if (IsPomProperties(innerEntry)) ReadPomProperties(innerEntry, file, deps);
                        }
                    }
                    catch (InvalidDataException) { }
                }
            }
        }
        catch (InvalidDataException) { }
        catch (IOException) { }
    }

    private static bool IsPomProperties(ZipArchiveEntry entry) =>
        entry.Name.Equals("pom.properties", StringComparison.Ordinal) &&
        entry.FullName.Contains("META-INF/maven/", StringComparison.Ordinal) &&
        entry.Length is > 0 and <= MaxPropertiesBytes;

    private static void ReadPomProperties(ZipArchiveEntry entry, string file, List<Dependency> deps)
    {
        using var memory = new MemoryStream();
        using (var source = entry.Open()) CopyAtMost(source, memory, MaxPropertiesBytes);

        string? group = null, artifact = null, version = null;
        foreach (var raw in Encoding.UTF8.GetString(memory.ToArray()).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            switch (line[..eq].Trim())
            {
                case "groupId": group = value; break;
                case "artifactId": artifact = value; break;
                case "version": version = value; break;
            }
        }
        if (IsCoordinate(group) && IsCoordinate(artifact) && IsCoordinate(version))
            Add(deps, "Maven", $"{group}:{artifact}", version!, file);
    }

    private static bool IsCoordinate(string? value) =>
        value is { Length: > 0 and <= 200 } && !value.Contains("${") && !value.Any(c => char.IsControl(c) || c is ' ' or ':');

    /// <summary>Copies no more than the limit, whatever size the archive claims for the entry.</summary>
    private static void CopyAtMost(Stream source, Stream destination, int limit)
    {
        var buffer = new byte[81920];
        var total = 0;
        int read;
        while (total < limit && (read = source.Read(buffer, 0, Math.Min(buffer.Length, limit - total))) > 0)
        {
            destination.Write(buffer, 0, read);
            total += read;
        }
    }

    private static void Add(List<Dependency> deps, string ecosystem, string name, string version, string file)
    {
        name = name.Trim();
        version = version.Trim();
        if (name.Length == 0 || version.Length == 0) return;
        deps.Add(new Dependency(ecosystem, name, version, file, 1));
    }
}
