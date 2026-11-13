using System.IO.Compression;
using System.Text;
using Cerberus.Scanning;

namespace Cerberus.Tests;

/// <summary>
/// Dependency inventory read from built artefacts. Each artefact is assembled
/// in the test — a few bytes of a Go binary, a small archive — so nothing
/// compiled is kept in the repository.
/// </summary>
public class ArtefactDependencyTests
{
    private static string[] Found(ScanSandbox sandbox, string path = ".")
    {
        var (deps, _) = Dependencies.Discover(Path.GetFullPath(Path.Combine(sandbox.Root, path)), sandbox.Root);
        return deps.Select(d => $"{d.Ecosystem} {d.Name}@{d.Version}").Order(StringComparer.Ordinal).ToArray();
    }

    // --- .NET ------------------------------------------------------------------------

    [Fact]
    public void A_deps_json_lists_the_packages_and_not_the_application_itself()
    {
        const string json = """
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
          "libraries": {
            "Shop.Api/1.0.0": { "type": "project", "serviceable": false },
            "Newtonsoft.Json/12.0.1": { "type": "package", "serviceable": true },
            "Microsoft.Data.Sqlite.Core/10.0.0": { "type": "package" },
            "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.0": { "type": "runtimepack" },
            "Broken": { "type": "package" }
          }
        }
        """;
        using var sandbox = new ScanSandbox().File("publish/Shop.Api.deps.json", json);
        var (deps, manifests) = Dependencies.Discover(sandbox.Root, sandbox.Root);

        Assert.Equal(["NuGet Microsoft.Data.Sqlite.Core@10.0.0", "NuGet Newtonsoft.Json@12.0.1"], Found(sandbox));
        Assert.Single(manifests);
        Assert.All(deps, d => Assert.Equal(1, d.Line));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"libraries": []}""")]
    [InlineData("[]")]
    public void A_deps_json_that_is_not_one_adds_nothing(string content)
    {
        using var sandbox = new ScanSandbox().File("App.deps.json", content);
        Assert.Empty(Found(sandbox));
    }

    // --- Go --------------------------------------------------------------------------

    private static readonly byte[] ModulesStart = [0x30, 0x77, 0xaf, 0x0c, 0x92, 0x74, 0x08, 0x02, 0x41, 0xe1, 0xc1, 0x07, 0xe6, 0xd6, 0x18, 0xe6];
    private static readonly byte[] ModulesEnd = [0xf9, 0x32, 0x43, 0x31, 0x86, 0x18, 0x20, 0x72, 0x00, 0x82, 0x42, 0x10, 0x41, 0x16, 0xd8, 0xf2];

    /// <summary>The parts of a Go binary the reader looks at: an executable signature, the build-info header, the module list.</summary>
    private static byte[] GoBinary(string? toolchain, string? modules, byte[]? signature = null)
    {
        var bytes = new List<byte>(signature ?? [0x7F, (byte)'E', (byte)'L', (byte)'F']);
        bytes.AddRange(new byte[60]);
        if (toolchain is not null)
        {
            bytes.Add(0xff);
            bytes.AddRange(Encoding.ASCII.GetBytes(" Go buildinf:"));
            bytes.Add(8);      // pointer size
            bytes.Add(0x2);    // flags: strings follow the header
            bytes.AddRange(new byte[16]);
            bytes.Add((byte)toolchain.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(toolchain));
        }
        if (modules is not null)
        {
            bytes.AddRange(new byte[7]);
            bytes.AddRange(ModulesStart);
            bytes.AddRange(Encoding.UTF8.GetBytes(modules));
            bytes.AddRange(ModulesEnd);
        }
        bytes.AddRange(new byte[40]);
        return [.. bytes];
    }

    private const string Modules =
        "path\tgithub.com/acme/shop/cmd/server\n" +
        "mod\tgithub.com/acme/shop\t(devel)\t\n" +
        "dep\tgithub.com/gin-gonic/gin\tv1.6.0\th1:abc=\n" +
        "dep\tgolang.org/x/text\tv0.3.0\th1:def=\n" +
        "=>\tgithub.com/acme/text\tv0.3.8\th1:ghi=\n" +
        "dep\tgithub.com/acme/local\tv0.0.0\n" +
        "=>\t../local\t(devel)\t\n" +
        "dep\tgolang.org/x/crypto\tv0.0.0-20200622213623-75b288015ac9\th1:jkl=\n" +
        "build\t-compiler=gc\n";

    [Fact]
    public void A_go_binary_gives_its_modules_and_its_toolchain()
    {
        using var sandbox = new ScanSandbox().Bytes("bin-out/server", GoBinary("go1.21.3", Modules));

        Assert.Equal(
            [
                "Go github.com/acme/local@0.0.0",                 // replaced by a local directory: the original is kept
                "Go github.com/acme/text@0.3.8",                  // a replace directive: what was linked
                "Go github.com/gin-gonic/gin@1.6.0",
                "Go golang.org/x/crypto@0.0.0-20200622213623-75b288015ac9",
                "Go stdlib@1.21.3",
            ],
            Found(sandbox));
    }

    [Theory]
    [InlineData("go1.22.0 X:boringcrypto", "1.22.0")]
    [InlineData("go1.20", "1.20")]
    public void The_toolchain_version_is_read_from_what_go_wrote(string written, string version)
    {
        using var sandbox = new ScanSandbox().Bytes("tool", GoBinary(written, null));
        Assert.Equal([$"Go stdlib@{version}"], Found(sandbox));
    }

    [Fact]
    public void A_go_binary_in_any_executable_format_is_read()
    {
        using var sandbox = new ScanSandbox()
            .Bytes("tool.exe", GoBinary(null, "dep\tgithub.com/a/b\tv1.0.0\th1:x=\n", [(byte)'M', (byte)'Z', 0x90, 0]))
            .Bytes("tool-mac", GoBinary(null, "dep\tgithub.com/c/d\tv2.0.0\th1:y=\n", [0xCF, 0xFA, 0xED, 0xFE]));
        Assert.Equal(["Go github.com/a/b@1.0.0", "Go github.com/c/d@2.0.0"], Found(sandbox));
    }

    [Fact]
    public void What_is_not_a_go_binary_adds_nothing()
    {
        using var sandbox = new ScanSandbox()
            .Bytes("plain-elf", GoBinary(null, null))
            // The same bytes without an executable signature: a data file is not searched.
            .Bytes("data.bin", GoBinary("go1.21.3", Modules, [1, 2, 3, 4]))
            // A module list that never ends, and a version string longer than the file.
            .Bytes("cut", [.. GoBinary(null, null), .. ModulesStart, .. "dep\tgithub.com/a/b\tv1.0.0\n"u8])
            .Bytes("cut2", [.. GoBinary("go1.21.3", null).AsSpan(0, 64 + 14 + 2 + 16 + 1 + 3)])
            .File("notes.txt", "dep\tgithub.com/a/b\tv1.0.0\n");
        Assert.Empty(Found(sandbox));
    }

    // --- Java ------------------------------------------------------------------------

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(content);
            }
        }
        return memory.ToArray();
    }

    private static byte[] Pom(string group, string artifact, string version) =>
        Encoding.UTF8.GetBytes($"#Generated by Maven\r\n#Tue Jan 01 00:00:00 UTC 2030\r\ngroupId={group}\r\nartifactId={artifact}\r\nversion={version}\r\n");

    [Fact]
    public void A_jar_gives_the_maven_artefacts_recorded_in_it()
    {
        var jar = Zip(
            ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n"u8.ToArray()),
            ("META-INF/maven/com.fasterxml.jackson.core/jackson-databind/pom.properties", Pom("com.fasterxml.jackson.core", "jackson-databind", "2.9.8")),
            ("META-INF/maven/org.apache.logging.log4j/log4j-core/pom.properties", Pom("org.apache.logging.log4j", "log4j-core", "2.14.1")),
            ("com/acme/App.class", [0xCA, 0xFE, 0xBA, 0xBE]),
            ("docs/pom.properties", Pom("not", "maven-metadata", "1.0")));
        using var sandbox = new ScanSandbox().Bytes("app/shaded.jar", jar);

        Assert.Equal(
            ["Maven com.fasterxml.jackson.core:jackson-databind@2.9.8", "Maven org.apache.logging.log4j:log4j-core@2.14.1"],
            Found(sandbox));
    }

    [Fact]
    public void Archives_nested_one_level_down_are_read_as_in_a_spring_boot_or_war_layout()
    {
        var library = Zip(("META-INF/maven/org.yaml/snakeyaml/pom.properties", Pom("org.yaml", "snakeyaml", "1.26")));
        var deeper = Zip(("lib/inner.jar", Zip(("META-INF/maven/too/deep/pom.properties", Pom("too", "deep", "9.9")))));
        var boot = Zip(
            ("META-INF/maven/com.acme/shop/pom.properties", Pom("com.acme", "shop", "1.0.0")),
            ("BOOT-INF/lib/snakeyaml-1.26.jar", library),
            ("BOOT-INF/lib/wrapper.jar", deeper),
            ("BOOT-INF/lib/broken.jar", "not a zip"u8.ToArray()));
        using var sandbox = new ScanSandbox().Bytes("shop.war", boot);

        Assert.Equal(["Maven com.acme:shop@1.0.0", "Maven org.yaml:snakeyaml@1.26"], Found(sandbox));
    }

    [Fact]
    public void Unfilled_or_malformed_coordinates_and_damaged_archives_add_nothing()
    {
        var jar = Zip(
            ("META-INF/maven/a/b/pom.properties", Pom("com.acme", "lib", "${project.version}")),
            ("META-INF/maven/c/d/pom.properties", "groupId=com.acme\nartifactId=half\n"u8.ToArray()),
            ("META-INF/maven/e/f/pom.properties", Pom("com acme", "sp ace", "1.0")));
        using var sandbox = new ScanSandbox()
            .Bytes("odd.jar", jar)
            .Bytes("broken.jar", "PK\u0003\u0004 truncated"u8.ToArray())
            .Bytes("empty.jar", []);
        Assert.Empty(Found(sandbox));
    }

    // --- the inventory around it -------------------------------------------------------

    [Fact]
    public void Artefacts_and_manifests_are_one_inventory_without_duplicates()
    {
        using var sandbox = new ScanSandbox()
            .File("go.mod", "module x\nrequire github.com/gin-gonic/gin v1.6.0\n")
            .Bytes("server", GoBinary(null, "dep\tgithub.com/gin-gonic/gin\tv1.6.0\th1:x=\ndep\tgithub.com/only/shipped\tv0.1.0\th1:y=\n"))
            .File("requirements.txt", "Django==2.2.0\n");
        var (deps, manifests) = Dependencies.Discover(sandbox.Root, sandbox.Root);

        Assert.Equal(
            ["Go github.com/gin-gonic/gin@1.6.0", "Go github.com/only/shipped@0.1.0", "PyPI Django@2.2.0"],
            Found(sandbox));
        Assert.Equal(3, manifests.Count);
        // The same module from two places is checked once, and attributed to the first file that named it.
        Assert.Equal("go.mod", deps.Single(d => d.Name == "github.com/gin-gonic/gin").File);
    }

    [Fact]
    public void Build_directories_are_skipped_unless_they_are_the_target()
    {
        using var sandbox = new ScanSandbox().File("bin/Release/App.deps.json",
            """{"libraries": {"Newtonsoft.Json/12.0.1": {"type": "package"}}}""");

        Assert.Empty(Found(sandbox));
        Assert.Equal(["NuGet Newtonsoft.Json@12.0.1"], Found(sandbox, "bin"));
    }
}
