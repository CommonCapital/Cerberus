using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cerberus.Api;
using Cerberus.Core.Models;
using Cerberus.Models;
using Cerberus.Triage;

namespace Cerberus.Tests;

public class ModelRegistryTests
{
    [Theory]
    [InlineData("claude-opus-5-5", ModelProvider.Anthropic, "claude-opus-5-5")]
    [InlineData("deepseek-chat", ModelProvider.DeepSeek, "deepseek-chat")]
    [InlineData("deepseek-reasoner", ModelProvider.DeepSeek, "deepseek-reasoner")]
    [InlineData("deepseek-r1", ModelProvider.Ollama, "deepseek-r1")]          // the open model, run locally
    [InlineData("llama3.1", ModelProvider.Ollama, "llama3.1")]
    [InlineData("echo", ModelProvider.Mock, "echo")]
    public void A_listed_model_is_served_by_its_provider(string id, ModelProvider provider, string name)
    {
        Assert.Equal((provider, name), (ModelRegistry.Find(id)!.Provider, ModelRegistry.Find(id)!.Name));
    }

    [Theory]
    [InlineData("ollama:qwen2.5-coder:7b", ModelProvider.Ollama, "qwen2.5-coder:7b")]
    [InlineData("ollama:hf.co/bartowski/Some-Model-GGUF:Q4_K_M", ModelProvider.Ollama, "hf.co/bartowski/Some-Model-GGUF:Q4_K_M")]
    [InlineData("Ollama:mistral-small", ModelProvider.Ollama, "mistral-small")]
    [InlineData("anthropic:claude-opus-4-8", ModelProvider.Anthropic, "claude-opus-4-8")]
    [InlineData("openai:gpt-4.1", ModelProvider.OpenAI, "gpt-4.1")]
    [InlineData("deepseek:deepseek-coder", ModelProvider.DeepSeek, "deepseek-coder")]
    public void Any_model_of_a_provider_can_be_named_with_the_providers_prefix(string id, ModelProvider provider, string name)
    {
        var model = ModelRegistry.Find(id)!;

        Assert.Equal((provider, name), (model.Provider, model.Name));
        Assert.Equal(name, ModelRegistry.NameOf(id));
        Assert.True(ModelRegistry.IsSupported(id));
    }

    [Theory]
    [InlineData("claude-opus-4-6", ModelProvider.Anthropic)]     // not listed any more, still Claude
    [InlineData("claude-sonnet-4-20250514", ModelProvider.Anthropic)]
    [InlineData("gpt-4o", ModelProvider.OpenAI)]
    [InlineData("chatgpt-4o-latest", ModelProvider.OpenAI)]
    [InlineData("o3-mini", ModelProvider.OpenAI)]
    public void A_model_of_a_family_that_belongs_to_one_provider_needs_no_prefix(string id, ModelProvider provider)
    {
        Assert.Equal(provider, ModelRegistry.Find(id)!.Provider);
        Assert.Equal(id, ModelRegistry.NameOf(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mistral")]                 // could be a local model, could be a slip of the hand: say which
    [InlineData("llama3.1:8b")]             // a tag on a listed name is another model: ollama:llama3.1:8b
    [InlineData("deepseek-coder")]          // DeepSeek's API or a local copy? deepseek:… or ollama:…
    [InlineData("ollama:")]
    [InlineData("groq:llama3")]             // not a provider this server has
    [InlineData("gpt-4 turbo")]
    [InlineData("ollama:model; rm -rf /")]
    [InlineData("ollama:../../etc/passwd\n")]
    [InlineData("-claude")]
    public void What_is_not_clearly_a_model_of_a_known_provider_is_not_guessed_at(string? id)
    {
        Assert.Null(ModelRegistry.Find(id));
        Assert.False(ModelRegistry.IsSupported(id));
    }

    [Fact]
    public void An_id_has_a_length_limit()
    {
        Assert.NotNull(ModelRegistry.Find("ollama:" + new string('m', ModelRegistry.MaxIdLength - 7)));
        Assert.Null(ModelRegistry.Find("ollama:" + new string('m', ModelRegistry.MaxIdLength)));
    }

    [Fact]
    public void The_default_is_a_listed_model_and_every_listed_model_resolves_to_itself()
    {
        Assert.Contains(ModelRegistry.Models, m => m.Id == ModelRegistry.DefaultModelId);
        Assert.All(ModelRegistry.Models, m => Assert.Same(m, ModelRegistry.Find(m.Id)));
        Assert.Equal(ModelRegistry.Models.Count, ModelRegistry.Models.Select(m => m.Id).Distinct().Count());
    }

    [Fact]
    public void Only_models_on_this_machine_count_as_local_and_windows_are_sized_by_provider()
    {
        Assert.True(FindingTriager.IsLocal(ModelRegistry.Find("ollama:anything")!.Provider));
        Assert.False(FindingTriager.IsLocal(ModelRegistry.Find("deepseek-chat")!.Provider));
        Assert.False(FindingTriager.IsLocal(ModelRegistry.Find("openai:gpt-4.1")!.Provider));

        Assert.Equal(200_000, ModelRegistry.ContextWindowOf("anthropic:claude-opus-4-8"));
        Assert.Equal(64_000, ModelRegistry.ContextWindowOf("deepseek-chat"));
        Assert.Equal(8_000, ModelRegistry.ContextWindowOf("ollama:qwen2.5-coder:7b"));
    }

    [Theory]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("claude-sonnet-5-5", true)]
    [InlineData("claude-haiku-5-5", true)]
    [InlineData("claude-fable-5-2", true)]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("claude-opus-4-6", true)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-haiku-4-5", false)]
    [InlineData("claude-sonnet-4-5", false)]
    [InlineData("claude-opus-4-1", false)]
    [InlineData("claude-sonnet-4-20250514", false)]     // a date, not version 4.20250514
    [InlineData("claude-3-5-sonnet-20241022", false)]
    [InlineData("claude-3-haiku-20240307", false)]
    [InlineData("claude-something-new", true)]
    public void Adaptive_thinking_is_sent_to_the_models_that_take_it(string model, bool adaptive)
    {
        Assert.Equal(adaptive, AnthropicProvider.UsesAdaptiveThinking(model));
    }
}

public class ProviderRoutingTests
{
    private static async Task<string> Text(IAsyncEnumerable<string> stream)
    {
        var text = new StringBuilder();
        await foreach (var delta in stream) text.Append(delta);
        return text.ToString();
    }

    [Fact]
    public async Task A_provider_is_handed_the_model_under_its_own_name()
    {
        var ollama = new ScriptedProvider(ModelProvider.Ollama, _ => "local");
        var deepseek = new ScriptedProvider(ModelProvider.DeepSeek, _ => "deep");
        var anthropic = new ScriptedProvider(ModelProvider.Anthropic, _ => "claude");
        var resolver = new ModelProviderResolver([ollama, deepseek, anthropic]);
        ChatCompletionRequest Ask(string model) => new(model, [new ChatMessage("user", "hi")], null, new ModelCredentials());

        Assert.Equal("local", await Text(resolver.Resolve("ollama:qwen2.5-coder:7b").StreamAsync(Ask("ollama:qwen2.5-coder:7b"))));
        Assert.Equal("deep", await Text(resolver.Resolve("deepseek-chat").StreamAsync(Ask("deepseek-chat"))));
        Assert.Equal("deep", await Text(resolver.Resolve("deepseek:deepseek-coder").StreamAsync(Ask("deepseek:deepseek-coder"))));
        Assert.Equal("claude", await Text(resolver.Resolve("anthropic:claude-opus-4-8").StreamAsync(Ask("anthropic:claude-opus-4-8"))));

        Assert.Equal("qwen2.5-coder:7b", Assert.Single(ollama.Requests).ModelId);
        Assert.Equal(["deepseek-chat", "deepseek-coder"], deepseek.Requests.Select(r => r.ModelId).ToArray());
        Assert.Equal("claude-opus-4-8", Assert.Single(anthropic.Requests).ModelId);

        var refused = Assert.Throws<InvalidOperationException>(() => resolver.Resolve("mistral"));
        Assert.Contains("provider:model", refused.Message);
    }

    private sealed class Captures(string sse) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }
    }

    private const string ReasonerStream =
        """
        data: {"choices":[{"delta":{"role":"assistant","reasoning_content":"The user asks for the file. "}}]}

        data: {"choices":[{"delta":{"content":"Reading it."}}]}

        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"readFile","arguments":"{\"path\":"}}]}}]}

        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a.py\"}"}}]}}]}

        data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":12,"completion_tokens":7}}

        data: [DONE]

        """;

    [Fact]
    public async Task DeepSeek_is_called_at_its_own_address_with_its_own_key_and_its_reasoning_and_tool_calls_are_read()
    {
        var handler = new Captures(ReasonerStream);
        var provider = new DeepSeekProvider(new HttpClient(handler));
        var tool = new ToolDefinition("readFile", "Reads.", JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""").RootElement.Clone());
        var request = new ChatRequest("deepseek-reasoner", "system", [ChatTurn.User("read a.py")], [tool], new ModelCredentials(DeepSeekApiKey: "sk-test-not-a-real-key"));

        var events = new List<ChatEvent>();
        await foreach (var e in provider.StreamChatAsync(request)) events.Add(e);

        Assert.Equal("https://api.deepseek.com/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test-not-a-real-key", handler.Request.Headers.Authorization.Parameter);
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal("deepseek-reasoner", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("readFile", sent.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());

        Assert.Contains(events, e => e is ReasoningDelta { Text: "The user asks for the file. " });
        Assert.Contains(events, e => e is TextDelta { Text: "Reading it." });
        var call = Assert.Single(events.OfType<ToolCallComplete>());
        Assert.Equal(("readFile", """{"path":"a.py"}"""), (call.Name, call.InputJson));
    }

    [Fact]
    public async Task Without_a_key_DeepSeek_is_not_called_and_the_error_says_what_to_do()
    {
        var before = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
        try
        {
            var handler = new Captures(ReasonerStream);
            var provider = new DeepSeekProvider(new HttpClient(handler));
            var request = new ChatRequest("deepseek-chat", null, [ChatTurn.User("hi")], [], new ModelCredentials());

            var problem = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in provider.StreamChatAsync(request)) { }
            });

            Assert.Contains("No DeepSeek API key", problem.Message);
            Assert.Contains("/keys", problem.Message);
            Assert.Null(handler.Request);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", before);
        }
    }

    [Theory]
    [InlineData("llama3.1:latest", "llama3.1")]
    [InlineData("llama3.1", "llama3.1")]
    [InlineData("llama3.1:8b", "ollama:llama3.1:8b")]
    [InlineData("qwen2.5-coder:7b", "ollama:qwen2.5-coder:7b")]
    [InlineData("mistral-small:latest", "ollama:mistral-small:latest")]
    public void An_installed_local_model_gets_an_id_that_can_be_chosen(string installed, string id)
    {
        Assert.Equal(id, ModelEndpoints.LocalId(installed));
        Assert.Equal(ModelProvider.Ollama, ModelRegistry.Find(id)!.Provider);
    }
}

[Collection("api")]
public class ModelSelectionApiTests(ApiHost host)
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static Dictionary<string, JsonElement> Providers(JsonElement listing) =>
        listing.GetProperty("providers").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!, p => p);

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(m => m.GetString()!).ToArray();

    [Fact]
    public async Task The_listing_says_what_can_be_chosen_and_what_is_ready_to_use()
    {
        var (client, _) = await host.NewUserAsync();

        var listing = await Json(await client.GetAsync("/models"));

        Assert.Equal("claude-opus-5-5", listing.GetProperty("default").GetString());
        var providers = Providers(listing);
        Assert.Equal(["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-2"], Strings(providers["anthropic"].GetProperty("models")));
        Assert.Equal(["deepseek-chat", "deepseek-reasoner"], Strings(providers["deepseek"].GetProperty("models")));
        Assert.Contains("gpt-5.4", Strings(providers["openai"].GetProperty("models")));
        Assert.Equal(("api", "deepseek:<model>"), (providers["deepseek"].GetProperty("kind").GetString(), providers["deepseek"].GetProperty("anyModel").GetString()));

        // The local provider lists what is installed, each under an id that can be chosen. Embedding models are not chat models.
        host.LocalModels.Installed = ["llama3.1:latest", "nomic-embed-text:latest", "qwen2.5-coder:7b"];
        var local = Providers(await Json(await client.GetAsync("/models")))["ollama"];
        Assert.Equal(("local", true, true), (local.GetProperty("kind").GetString(), local.GetProperty("ready").GetBoolean(), local.GetProperty("reachable").GetBoolean()));

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/models")).StatusCode);
    }

    [Fact]
    public async Task Installed_local_models_are_listed_and_a_silent_local_server_is_said_to_be_silent()
    {
        var (client, _) = await host.NewUserAsync();
        var before = host.LocalModels.Installed;
        try
        {
            host.LocalModels.Installed = ["llama3.1:latest", "qwen2.5-coder:7b"];
            var installed = Providers(await Json(await client.GetAsync("/models")))["ollama"];
            Assert.Equal(["llama3.1", "ollama:qwen2.5-coder:7b"], Strings(installed.GetProperty("models")));

            host.LocalModels.Installed = [];
            var none = Providers(await Json(await client.GetAsync("/models")))["ollama"];
            Assert.Equal((false, true), (none.GetProperty("ready").GetBoolean(), none.GetProperty("reachable").GetBoolean()));

            host.LocalModels.Installed = null;
            var silent = Providers(await Json(await client.GetAsync("/models")))["ollama"];
            Assert.Equal((false, false), (silent.GetProperty("ready").GetBoolean(), silent.GetProperty("reachable").GetBoolean()));
            Assert.Contains("llama3.1", Strings(silent.GetProperty("models")));   // suggestions only
        }
        finally
        {
            host.LocalModels.Installed = before;
        }
    }

    [Fact]
    public async Task A_stored_key_makes_its_provider_ready_for_its_owner_only()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();

        var saved = await client.PutAsJsonAsync("/settings/keys", new { provider = "deepseek", apiKey = "sk-test-not-a-real-key-0123456789" });
        Assert.True(saved.IsSuccessStatusCode);

        var mine = Providers(await Json(await client.GetAsync("/models")))["deepseek"];
        Assert.Equal((true, "yours"), (mine.GetProperty("ready").GetBoolean(), mine.GetProperty("keySource").GetString()));
        var keys = (await Json(await client.GetAsync("/settings/keys"))).EnumerateArray().ToDictionary(k => k.GetProperty("provider").GetString()!, k => k.GetProperty("hasKey").GetBoolean());
        Assert.True(keys["deepseek"]);
        Assert.DoesNotContain("sk-test-not-a-real-key", await (await client.GetAsync("/models")).Content.ReadAsStringAsync());

        var theirs = Providers(await Json(await other.GetAsync("/models")))["deepseek"];
        Assert.NotEqual("yours", theirs.GetProperty("keySource").GetString());
    }

    [Fact]
    public async Task A_chat_can_use_any_local_model_by_name_and_an_unknown_bare_name_is_refused()
    {
        var (client, _) = await host.NewUserAsync();
        var session = (await Json(await client.PostAsJsonAsync("/sessions", new { title = "chat" }))).GetProperty("id").GetString()!;
        var marker = $"m{Guid.NewGuid():n}";
        object Body(string model) => new { id = session, mode = "PLAN", model, messages = new[] { new { id = "u1", role = "user", parts = new[] { new { type = "text", text = $"hello {marker}" } } } } };

        var response = await client.PostAsJsonAsync("/chat", Body("ollama:my-own-model:7b"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var said = string.Concat((await response.Content.ReadAsStringAsync()).Split('\n')
            .Where(line => line.StartsWith("data: {", StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line[6..]).RootElement)
            .Where(chunk => chunk.GetProperty("type").GetString() == "text-delta")
            .Select(chunk => chunk.GetProperty("delta").GetString()));
        Assert.Contains($"Echo: hello {marker}", said);
        lock (host.Tools.Requests)
        {
            // The local provider was asked for the model under the name it knows.
            Assert.Equal("my-own-model:7b", host.Tools.Requests.Last(r => r.Turns.Any(t => ChatEvents.TextOf(t).Contains(marker))).ModelId);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/chat", Body("my-own-model"))).StatusCode);
    }

    [Fact]
    public async Task Code_sent_to_DeepSeek_needs_the_same_consent_as_any_cloud_model_and_a_local_model_none()
    {
        var (client, _) = await host.NewUserAsync();

        var cloud = await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "svc", model = "deepseek-chat" }));
        Assert.Equal("AwaitingApproval", cloud.GetProperty("job").GetProperty("state").GetString());
        Assert.Contains("send-code-to-cloud-model", cloud.GetProperty("approval").GetProperty("kinds").EnumerateArray().Select(k => k.GetString()));

        var alsoCloud = await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "svc", model = "deepseek:deepseek-coder" }));
        Assert.Equal("AwaitingApproval", alsoCloud.GetProperty("job").GetProperty("state").GetString());

        var local = await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "svc", model = "ollama:my-own-model:7b" }));
        Assert.False(local.TryGetProperty("approval", out _));
        Assert.NotEqual("AwaitingApproval", local.GetProperty("state").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "svc", model = "my-own-model" })).StatusCode);
    }
}
