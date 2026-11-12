using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerberus.Api.Agents;
using Cerberus.Api.Search;
using Cerberus.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Cerberus.Tests;

/// <summary>
/// An embedder without a model: each word of the text adds to one dimension
/// (by its hash), so texts sharing words are close. Enough to test everything
/// around the model.
/// </summary>
public sealed class WordEmbedder : IEmbedder
{
    public string Model => "test-words";

    public List<(string Text, EmbeddingPurpose Purpose)> Embedded { get; } = [];

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, EmbeddingPurpose purpose, CancellationToken ct)
    {
        lock (Embedded) Embedded.AddRange(inputs.Select(text => (text, purpose)));
        return Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(Embed).ToList());
    }

    public static float[] Embed(string text)
    {
        var vector = new float[128];
        foreach (Match word in Regex.Matches(text.ToLowerInvariant(), "[a-z]{3,}"))
        {
            var hash = 17;
            foreach (var c in word.Value) hash = unchecked(hash * 31 + c);
            vector[(hash & 0x7fffffff) % vector.Length] += 1;
        }
        return vector;
    }
}

public class SemanticSearchUnitTests
{
    [Fact]
    public void Vectors_survive_storage_and_closeness_is_the_cosine()
    {
        float[] vector = [3, 4, 0];
        var unit = Vectors.Normalize(vector);

        Assert.Equal([0.6f, 0.8f, 0f], unit);
        Assert.Equal(unit, Vectors.FromBytes(Vectors.ToBytes(unit)));
        Assert.Equal(1f, Vectors.Dot(unit, unit), 5);
        Assert.Equal(0f, Vectors.Dot(unit, Vectors.Normalize([0, 0, 2])), 5);
        Assert.Equal([0f, 0f], Vectors.Normalize([0, 0]));   // nothing to scale
    }

    [Fact]
    public void A_file_is_cut_at_its_definitions_and_each_piece_carries_its_path_and_name()
    {
        string[] lines =
        [
            "import hashlib",
            "SECRET_ROUNDS = 12",
            "",
            "def hash_password(password):",
            "    return hashlib.sha256(password.encode()).hexdigest()",
            "",
            "class Session:",
            "    def is_expired(self):",
            "        return self.age > 3600",
        ];

        var chunks = CodeChunker.Chunk("auth/passwords.py", lines, "h1");

        Assert.Equal(
            ["1-3 ", "4-6 function hash_password", "8-9 method is_expired"],
            chunks.Select(c => $"{c.StartLine}-{c.EndLine} {c.Name}").ToArray());   // the bare "class Session:" line says nothing on its own
        Assert.StartsWith("auth/passwords.py — function hash_password\ndef hash_password(password):", chunks[1].Text);
        Assert.All(chunks, c => Assert.Equal(("auth/passwords.py", "h1"), (c.File, c.FileHash)));
    }

    [Fact]
    public void The_comment_above_a_definition_belongs_to_its_chunk_not_to_the_code_before_it()
    {
        string[] lines =
        [
            "public class Workspace",
            "{",
            "    private int _count = 0;",
            "",
            "    /// <summary>Full path of a path inside the target; links are never followed.</summary>",
            "    [Obsolete]",
            "    public string Resolve(string relative)",
            "    {",
            "        return Check(relative);",
            "    }",
            "",
            "    // Counts how often it was used.",
            "    public int Used() { return _count; }",
            "}",
        ];

        var chunks = CodeChunker.Chunk("Workspace.cs", lines, "h");

        var resolve = chunks.Single(c => c.Name == "method Resolve");
        Assert.Equal((5, 11), (resolve.StartLine, resolve.EndLine));
        Assert.Contains("links are never followed", resolve.Text);
        Assert.DoesNotContain("links are never followed", chunks.Single(c => c.Name == "class Workspace").Text);
        Assert.Equal(12, chunks.Single(c => c.Name == "method Used").StartLine);
    }

    [Fact]
    public void A_file_without_definitions_is_cut_into_windows_and_long_pieces_are_split()
    {
        var config = Enumerable.Range(1, 150).Select(i => $"setting_{i}: value number {i}").ToArray();
        Assert.Equal(["1-60", "61-120", "121-150"], CodeChunker.Chunk("deploy/values.yaml", config, "h").Select(c => $"{c.StartLine}-{c.EndLine}").ToArray());

        var longFunction = new[] { "def big():" }.Concat(Enumerable.Range(1, 200).Select(i => $"    step_{i} = compute({i})")).ToArray();
        var pieces = CodeChunker.Chunk("big.py", longFunction, "h");
        Assert.Equal(["1-120", "121-201"], pieces.Select(c => $"{c.StartLine}-{c.EndLine}").ToArray());
        Assert.All(pieces, c => Assert.True(c.Text.Length <= CodeChunker.MaxChunkChars + 100));
    }

    [Fact]
    public void A_credential_in_the_code_is_not_what_gets_embedded()
    {
        var chunks = CodeChunker.Chunk("settings.py", ["aws_key = \"AKIAIOSFODNN7EXAMPLE\"", "region = \"eu-west-1\"", "bucket = \"reports\""], "h");

        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", chunks[0].Text);
        Assert.Contains("region", chunks[0].Text);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:11434/v1", true)]
    [InlineData("http://[::1]:11434/v1", true)]
    [InlineData("http://ollama:11434/v1", true)]            // the compose service
    [InlineData("https://api.example.com/v1", false)]
    [InlineData("http://10.0.0.5:11434/v1", false)]
    [InlineData("not a url", false)]
    public void Only_an_embedding_server_on_this_machine_is_used_unless_allowed(string url, bool local)
    {
        Assert.Equal(local, OllamaEmbedder.IsLocal(url));
    }

    [Fact]
    public async Task A_remote_embedding_server_is_refused_before_any_code_is_sent()
    {
        var handler = new CountingHandler();
        var embedder = new OllamaEmbedder(new HttpClient(handler), new EmbeddingOptions("nomic-embed-text", "https://api.example.com/v1"));

        var problem = await Assert.ThrowsAsync<InvalidOperationException>(() => embedder.EmbedAsync(["secret code"], EmbeddingPurpose.Document, default));

        Assert.Contains("not on this machine", problem.Message);
        Assert.Equal(0, handler.Calls);
        Assert.Equal("search_query: ", OllamaEmbedder.Prefix("nomic-embed-text", EmbeddingPurpose.Query));
        Assert.Equal("search_document: ", OllamaEmbedder.Prefix("nomic-embed-text:latest", EmbeddingPurpose.Document));
        Assert.Equal("", OllamaEmbedder.Prefix("mxbai-embed-large", EmbeddingPurpose.Query));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

[Collection("api")]
public class SemanticSearchApiTests(ApiHost host)
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> RunAsync(HttpClient client, object body)
    {
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", body));
        for (var i = 0; i < 400; i++)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/jobs/{submitted.GetProperty("id").GetString()}");
            if (job.GetProperty("state").GetString() is "Succeeded" or "Failed" or "Cancelled" or "TimedOut") return job;
            await Task.Delay(40);
        }
        throw new TimeoutException("job did not finish");
    }

    /// <summary>A small project under the scan root: authentication, storage and a deployment file.</summary>
    private static (string Path, string Root) Plant()
    {
        var name = "search-" + Guid.NewGuid().ToString("n")[..8];
        var root = Path.Combine(Environment.GetEnvironmentVariable("CERBERUS_SCAN_ROOT")!, name);
        Directory.CreateDirectory(Path.Combine(root, "auth"));
        Directory.CreateDirectory(Path.Combine(root, "store"));
        File.WriteAllText(Path.Combine(root, "auth", "passwords.py"),
            "import hashlib\n\ndef hash_password(password, salt):\n    return hashlib.sha256((salt + password).encode()).hexdigest()\n\ndef verify_password(password, salt, stored):\n    return hash_password(password, salt) == stored\n");
        File.WriteAllText(Path.Combine(root, "store", "invoices.py"),
            "def load_invoice(database, invoice_number):\n    return database.query('select * from invoices where number = ' + invoice_number)\n\ndef list_invoices(database, customer):\n    return database.query('select * from invoices where customer = ?', customer)\n");
        File.WriteAllText(Path.Combine(root, "deploy.yaml"), "replicas: 3\nimage: registry.local/shop:1.4\nresources:\n  memory: 512Mi\n");
        return (name, root);
    }

    private int EmbeddedDocuments(string marker)
    {
        lock (host.Embedder.Embedded) return host.Embedder.Embedded.Count(e => e.Purpose == EmbeddingPurpose.Document && e.Text.Contains(marker));
    }

    [Fact]
    public async Task A_target_is_indexed_and_then_searched_by_what_the_code_does()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();
        var (path, _) = Plant();

        // No index yet: searching says how to get one.
        var none = await client.PostAsJsonAsync("/code-search", new { path, query = "password hashing" });
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        var job = await RunAsync(client, new { type = "index", path });
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());

        var index = Assert.Single((await client.GetFromJsonAsync<JsonElement>("/code-index")).EnumerateArray());
        Assert.Equal(job.GetProperty("resultId").GetString(), index.GetProperty("id").GetString());
        Assert.Equal((path, "test-words", 128, 3, 5), (index.GetProperty("targetPath").GetString(), index.GetProperty("model").GetString(),
            index.GetProperty("dimensions").GetInt32(), index.GetProperty("fileCount").GetInt32(), index.GetProperty("chunkCount").GetInt32()));

        // The closest code comes first, with where it is and what it looks like now.
        var found = await Json(await client.PostAsJsonAsync("/code-search", new { path, query = "verify a password against the stored hash", limit = 2 }));
        var hits = found.GetProperty("hits").EnumerateArray().ToList();
        Assert.Equal(2, hits.Count);
        Assert.Equal(("auth/passwords.py", "function verify_password", 6), (hits[0].GetProperty("file").GetString(), hits[0].GetProperty("name").GetString(), hits[0].GetProperty("startLine").GetInt32()));
        Assert.StartsWith("def verify_password(password, salt, stored):", hits[0].GetProperty("preview").GetString());
        Assert.False(hits[0].GetProperty("stale").GetBoolean());
        Assert.True(hits[0].GetProperty("score").GetDouble() >= hits[1].GetProperty("score").GetDouble());

        var invoices = await Json(await client.PostAsJsonAsync("/code-search", new { path, query = "load invoice by number from the database" }));
        Assert.Equal("store/invoices.py", invoices.GetProperty("hits")[0].GetProperty("file").GetString());

        // A narrower path searches the same index, with paths relative to it.
        var narrow = await Json(await client.PostAsJsonAsync("/code-search", new { path = $"{path}/store", query = "password hash invoice" }));
        Assert.All(narrow.GetProperty("hits").EnumerateArray(), h => Assert.Equal("invoices.py", h.GetProperty("file").GetString()));

        // Private, validated, audited — and nothing left this machine.
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/code-search", new { path, query = "password" })).StatusCode);
        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/code-index")).GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/code-search", new { path, query = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/code-search", new { path = "../x", query = "x" })).StatusCode);
        var audit = (await client.GetFromJsonAsync<JsonElement>("/audit?limit=20")).EnumerateArray().Single(e => e.GetProperty("action").GetString() == "index.build");
        Assert.Contains("no code left this machine", audit.GetProperty("detail").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/code-index/{index.GetProperty("id").GetString()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/code-index/{index.GetProperty("id").GetString()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/code-search", new { path, query = "password" })).StatusCode);
    }

    [Fact]
    public async Task Indexing_again_embeds_only_what_changed_and_forgets_what_is_gone()
    {
        var (client, _) = await host.NewUserAsync();
        var (path, root) = Plant();
        await RunAsync(client, new { type = "index", path });
        int Count(string file) => EmbeddedDocuments($"{file}");
        var (passwordsBefore, invoicesBefore) = (Count("auth/passwords.py"), Count("store/invoices.py"));

        // One file changes, one is deleted, one is added.
        File.AppendAllText(Path.Combine(root, "auth", "passwords.py"), "\ndef rotate_salt(user):\n    return generate_new_salt_for(user)\n");
        File.Delete(Path.Combine(root, "deploy.yaml"));
        File.WriteAllText(Path.Combine(root, "store", "refunds.py"), "def refund_payment(order):\n    return order.payment.refund_everything()\n");
        // Until it is re-indexed, a hit in the changed file says so.
        var stale = await Json(await client.PostAsJsonAsync("/code-search", new { path, query = "verify password stored hash", limit = 1 }));
        Assert.True(stale.GetProperty("hits")[0].GetProperty("stale").GetBoolean());

        var job = await RunAsync(client, new { type = "index", path });
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());

        Assert.Equal(invoicesBefore, Count("store/invoices.py"));          // unchanged: not embedded again
        Assert.True(Count("auth/passwords.py") > passwordsBefore);          // changed: embedded again
        var index = Assert.Single((await client.GetFromJsonAsync<JsonElement>("/code-index")).EnumerateArray());   // the same index, refreshed
        Assert.Equal(3, index.GetProperty("fileCount").GetInt32());

        var events = await client.GetStringAsync($"/jobs/{job.GetProperty("id").GetString()}/events?after=0");
        Assert.Contains("1 file(s) are unchanged since the last index and keep their vectors.", events);

        var refund = await Json(await client.PostAsJsonAsync("/code-search", new { path, query = "refund a payment for an order", limit = 1 }));
        Assert.Equal("store/refunds.py", refund.GetProperty("hits")[0].GetProperty("file").GetString());
        var all = await Json(await client.PostAsJsonAsync("/code-search", new { path, query = "replicas image registry memory", limit = 30 }));
        Assert.DoesNotContain(all.GetProperty("hits").EnumerateArray(), h => h.GetProperty("file").GetString() == "deploy.yaml");
        Assert.Contains(all.GetProperty("hits").EnumerateArray(), h => h.GetProperty("name").GetString() == "function rotate_salt" && !h.GetProperty("stale").GetBoolean());
    }

    [Fact]
    public async Task An_agent_gets_the_search_tool_only_when_its_target_has_an_index()
    {
        var (client, _) = await host.NewUserAsync();
        var (path, _) = Plant();
        ChatRequest FirstRequest(string marker)
        {
            lock (host.Tools.Requests) return host.Tools.Requests.Last(r => r.Turns.Count == 1 && ChatEvents.TextOf(r.Turns[0]).Contains($"\"{marker}\""));
        }

        // Without an index: no such tool, no mention of it.
        Assert.Equal("Succeeded", (await RunAsync(client, new { type = "sast", path, model = "llama3.1" })).GetProperty("state").GetString());
        var without = FirstRequest(path);
        Assert.DoesNotContain(without.Tools, t => t.Name == "searchCode");
        Assert.DoesNotContain("searchCode", ChatEvents.TextOf(without.Turns[0]));

        // With one — also for a directory below the indexed one.
        await RunAsync(client, new { type = "index", path });
        Assert.Equal("Succeeded", (await RunAsync(client, new { type = "sast", path = $"{path}/auth", model = "llama3.1" })).GetProperty("state").GetString());
        var with = FirstRequest($"{path}/auth");
        Assert.Contains(with.Tools, t => t.Name == "searchCode");
        Assert.Contains("A semantic index of this code exists", ChatEvents.TextOf(with.Turns[0]));
    }

    [Fact]
    public async Task The_search_tool_answers_an_agent_with_leads_inside_its_own_target()
    {
        var (client, token) = await host.NewUserAsync();
        var (path, _) = Plant();
        await RunAsync(client, new { type = "index", path });
        var search = (CodeSearch)host.Services.GetService(typeof(CodeSearch))!;
        string userId;
        using (var scope = ((IServiceProvider)host.Services).CreateScope())
        {
            userId = scope.ServiceProvider.GetRequiredService<Cerberus.Data.CerberusDbContext>().AuthSessions.Single(a => a.Token == token).UserId;
        }
        static JsonElement J(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

        var tool = (await search.ToolForAsync(userId, $"{path}/auth", default))!;
        var answer = await tool.RunAsync(J(new { query = "hash a password with a salt", limit = 3 }), default);

        Assert.Contains("passwords.py:3-5  function hash_password", answer);
        Assert.Contains("    def hash_password(password, salt):", answer);
        Assert.DoesNotContain("invoices.py", answer);   // outside the agent's target
        Assert.Contains("read the code before relying on a hit", answer);
        await Assert.ThrowsAsync<AgentToolException>(() => tool.RunAsync(J(new { query = "  " }), default));
        Assert.Null(await search.ToolForAsync(userId, "no-index-here", default));
    }
}
