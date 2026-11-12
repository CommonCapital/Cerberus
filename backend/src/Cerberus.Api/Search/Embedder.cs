using System.Net.Http.Json;
using System.Text.Json;

namespace Cerberus.Api.Search;

public enum EmbeddingPurpose
{
    /// <summary>A piece of code being indexed.</summary>
    Document,

    /// <summary>What someone is looking for.</summary>
    Query,
}

/// <param name="Model">The embedding model, as the local server knows it.</param>
/// <param name="BaseUrl">An OpenAI-compatible endpoint (<c>…/v1</c>), by default the local Ollama.</param>
/// <param name="AllowRemote">Whether <paramref name="BaseUrl"/> may be another machine. Off by default: indexing sends all the code there.</param>
public sealed record EmbeddingOptions(string Model, string BaseUrl, bool AllowRemote = false);

/// <summary>Turns text into vectors whose closeness reflects closeness in meaning.</summary>
public interface IEmbedder
{
    string Model { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, EmbeddingPurpose purpose, CancellationToken ct);
}

/// <summary>
/// Embeddings from a local model server (Ollama's OpenAI-compatible
/// <c>/v1/embeddings</c>). Local on purpose: building an index sends every
/// chunk of the code under review to the embedder, so unless explicitly
/// allowed it refuses to talk to anything but this machine.
/// </summary>
public sealed class OllamaEmbedder(HttpClient http, EmbeddingOptions options) : IEmbedder
{
    private const int BatchSize = 32;

    public string Model => options.Model;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, EmbeddingPurpose purpose, CancellationToken ct)
    {
        if (!options.AllowRemote && !IsLocal(options.BaseUrl))
        {
            throw new InvalidOperationException(
                $"The embedding server ({options.BaseUrl}) is not on this machine. Indexing would send the code there; set CERBERUS_EMBEDDING_ALLOW_REMOTE=true if that is intended.");
        }

        var vectors = new List<float[]>(inputs.Count);
        for (var start = 0; start < inputs.Count; start += BatchSize)
        {
            var batch = inputs.Skip(start).Take(BatchSize).Select(text => Prefix(options.Model, purpose) + text).ToList();
            HttpResponseMessage response;
            try
            {
                response = await http.PostAsJsonAsync($"{options.BaseUrl.TrimEnd('/')}/embeddings", new { model = options.Model, input = batch }, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    $"The embedding server at {options.BaseUrl} cannot be reached. Is Ollama running, and is the model pulled (ollama pull {options.Model})?", ex);
            }
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync(ct);
                    throw new InvalidOperationException(
                        $"The embedding server answered {(int)response.StatusCode}: {(detail.Length <= 300 ? detail : detail[..300])}");
                }
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var data = body.RootElement.GetProperty("data").EnumerateArray()
                    .OrderBy(item => item.TryGetProperty("index", out var index) ? index.GetInt32() : 0)
                    .Select(item => item.GetProperty("embedding").EnumerateArray().Select(value => value.GetSingle()).ToArray())
                    .ToList();
                if (data.Count != batch.Count) throw new InvalidOperationException("The embedding server returned a different number of vectors than texts.");
                vectors.AddRange(data);
            }
        }
        return vectors;
    }

    /// <summary>Some models are trained with a task prefix and search noticeably worse without it.</summary>
    public static string Prefix(string model, EmbeddingPurpose purpose) =>
        model.StartsWith("nomic-embed", StringComparison.OrdinalIgnoreCase)
            ? purpose == EmbeddingPurpose.Query ? "search_query: " : "search_document: "
            : "";

    public static bool IsLocal(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        (uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "host.docker.internal" || uri.Host == "ollama");
}

/// <summary>Vectors as they are stored and compared.</summary>
public static class Vectors
{
    /// <summary>Scales a vector to length 1, so that a dot product is the cosine similarity.</summary>
    public static float[] Normalize(float[] vector)
    {
        var length = MathF.Sqrt(vector.Sum(v => v * v));
        if (length == 0) return vector;
        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) result[i] = vector[i] / length;
        return result;
    }

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = 0f;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    public static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, vector.Length * sizeof(float));
        return vector;
    }
}
