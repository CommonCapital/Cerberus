using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Cerberus.Api.Agents;
using Cerberus.Api.Audit;
using Cerberus.Api.Auth;
using Cerberus.Authorization;
using Cerberus.Core.Models;
using Cerberus.Data;
using Cerberus.Jobs;
using Cerberus.Scanning;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Search;

public sealed record CodeHit(string File, int StartLine, int EndLine, string Name, double Score, string Preview, bool Stale);

/// <summary>
/// Builds the semantic index of a target (PLAN.md §20): cuts the code into
/// chunks, embeds them with a local model, and stores location and vector.
/// Re-indexing embeds only what changed: a file whose content is the same
/// keeps its vectors.
/// </summary>
public sealed class CodeIndexer(IEmbedder embedder, CerberusDbContext db, ScanOptions scanOptions, AuditLog audit, CodeSearch search)
{
    public async Task<CodeIndex> BuildAsync(Job job, IRunReporter reporter, CancellationToken ct)
    {
        var target = ScopePolicy.Normalize(TargetType.Path, job.TargetPath)
            ?? throw new InvalidOperationException("The target path is not valid.");
        var workspace = new CodeWorkspace(scanOptions.ScanRoot, job.TargetPath, redact: false);

        await reporter.StepAsync("chunk", "Cut the code into chunks", StepStatus.Running);
        var files = CodeChunker.Files(workspace).ToList();
        await reporter.StepAsync("chunk", "Cut the code into chunks", StepStatus.Completed);

        var index = await db.CodeIndexes.FirstOrDefaultAsync(i => i.UserId == job.UserId && i.TargetPath == target && i.Model == embedder.Model, ct);
        if (index is null)
        {
            index = new CodeIndex { UserId = job.UserId, EngagementId = job.EngagementId, TargetPath = target, Model = embedder.Model };
            db.CodeIndexes.Add(index);
            await db.SaveChangesAsync(ct);
        }

        // What is already indexed, by file and the content it had then.
        var known = (await db.CodeChunks.AsNoTracking().Where(c => c.IndexId == index.Id).Select(c => new { c.File, c.FileHash }).Distinct().ToListAsync(ct))
            .GroupBy(c => c.File).ToDictionary(g => g.Key, g => g.First().FileHash, StringComparer.Ordinal);
        var current = files.ToDictionary(f => f.File, f => f.Hash, StringComparer.Ordinal);

        var gone = known.Keys.Where(file => !current.TryGetValue(file, out var hash) || hash != known[file]).ToList();
        var changed = files.Where(f => !known.TryGetValue(f.File, out var hash) || hash != f.Hash).ToList();
        var chunks = changed.SelectMany(f => CodeChunker.Chunk(f.File, f.Lines, f.Hash)).Take(CodeChunker.MaxChunks).ToList();

        var title = $"Embed {chunks.Count} chunk(s) with {embedder.Model}";
        await reporter.StepAsync("embed", title, StepStatus.Running);
        if (files.Count - changed.Count > 0)
        {
            await reporter.EventAsync(RunEventTypes.Note, new { message = $"{files.Count - changed.Count} file(s) are unchanged since the last index and keep their vectors." });
        }

        // Stale chunks go first, so a search during the rebuild never returns a location from the old content of a changed file.
        foreach (var batch in gone.Chunk(200))
        {
            await db.CodeChunks.Where(c => c.IndexId == index.Id && batch.Contains(c.File)).ExecuteDeleteAsync(ct);
        }

        var dimensions = index.Dimensions;
        const int batchSize = 64;
        for (var done = 0; done < chunks.Count; done += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = chunks.Skip(done).Take(batchSize).ToList();
            var vectors = await embedder.EmbedAsync(batch.Select(c => c.Text).ToList(), EmbeddingPurpose.Document, ct);
            for (var i = 0; i < batch.Count; i++)
            {
                dimensions = vectors[i].Length;
                db.CodeChunks.Add(new CodeChunk
                {
                    IndexId = index.Id,
                    File = batch[i].File,
                    FileHash = batch[i].FileHash,
                    StartLine = batch[i].StartLine,
                    EndLine = batch[i].EndLine,
                    Name = batch[i].Name,
                    Embedding = Vectors.ToBytes(Vectors.Normalize(vectors[i])),
                });
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            await reporter.ProgressAsync(Math.Min(chunks.Count, done + batch.Count), chunks.Count, $"{Math.Min(chunks.Count, done + batch.Count)} of {chunks.Count} chunk(s) embedded.");
        }
        await reporter.StepAsync("embed", title, StepStatus.Completed);

        var stored = await db.CodeIndexes.FirstAsync(i => i.Id == index.Id, ct);
        stored.Dimensions = dimensions;
        stored.FileCount = files.Count;
        stored.ChunkCount = await db.CodeChunks.CountAsync(c => c.IndexId == index.Id, ct);
        stored.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        search.Forget(stored.Id);

        await audit.AppendAsync(job.UserId, "index.build", AuditOutcome.Ok,
            $"Semantic index {stored.Id} of \"{job.TargetPath}\": {stored.FileCount} file(s), {stored.ChunkCount} chunk(s), {chunks.Count} embedded now with {embedder.Model} (a local model; no code left this machine).",
            job.EngagementId, job.TargetPath, CapabilityTier.T0, CancellationToken.None);
        return stored;
    }
}

/// <summary>
/// Searches a semantic index: the question is embedded with the same model
/// and compared to every chunk's vector. Small enough to do in memory — a
/// target has thousands of chunks, not millions — so no vector database is needed.
/// </summary>
public sealed class CodeSearch(IEmbedder embedder, IServiceScopeFactory scopes, ScanOptions scanOptions)
{
    private sealed record Loaded(DateTime UpdatedAt, CodeChunk[] Chunks, float[][] Vectors);

    private readonly ConcurrentDictionary<string, Loaded> _cache = new();

    public void Forget(string indexId) => _cache.TryRemove(indexId, out _);

    /// <summary>
    /// The index that covers a target: one built for exactly that path, or for a
    /// directory above it (the nearest). Null when there is none.
    /// </summary>
    public async Task<CodeIndex?> FindAsync(string userId, string targetPath, CancellationToken ct)
    {
        var target = ScopePolicy.Normalize(TargetType.Path, targetPath);
        if (target is null) return null;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        var candidates = await db.CodeIndexes.AsNoTracking().Where(i => i.UserId == userId && i.Model == embedder.Model && i.ChunkCount > 0).ToListAsync(ct);
        return candidates
            .Where(i => i.TargetPath == target || i.TargetPath.Length == 0 || target.StartsWith(i.TargetPath + "/", StringComparison.Ordinal))
            .OrderByDescending(i => i.TargetPath.Length).FirstOrDefault();
    }

    /// <summary>The chunks closest in meaning to the query, among those under <paramref name="targetPath"/>; paths are relative to it.</summary>
    public async Task<IReadOnlyList<CodeHit>> SearchAsync(CodeIndex index, string targetPath, string query, int limit, CancellationToken ct)
    {
        var target = ScopePolicy.Normalize(TargetType.Path, targetPath) ?? "";
        // The part of the path between the index's root and the target: hits must lie under it.
        var within = target.Length > index.TargetPath.Length ? target[(index.TargetPath.Length == 0 ? 0 : index.TargetPath.Length + 1)..] : "";
        var loaded = await LoadAsync(index, ct);
        var wanted = Vectors.Normalize((await embedder.EmbedAsync([query], EmbeddingPurpose.Query, ct))[0]);

        var best = new PriorityQueue<int, float>();
        limit = Math.Clamp(limit, 1, 30);
        for (var i = 0; i < loaded.Chunks.Length; i++)
        {
            if (within.Length > 0 && !loaded.Chunks[i].File.StartsWith(within + "/", StringComparison.Ordinal)) continue;
            var score = Vectors.Dot(loaded.Vectors[i], wanted);
            if (best.Count < limit) best.Enqueue(i, score);
            else if (best.TryPeek(out _, out var lowest) && score > lowest) best.EnqueueDequeue(i, score);
        }

        var workspace = new CodeWorkspace(scanOptions.ScanRoot, index.TargetPath.Length == 0 ? "." : index.TargetPath, redact: false);
        var hits = new List<CodeHit>();
        while (best.TryDequeue(out var i, out var score))
        {
            var chunk = loaded.Chunks[i];
            var (preview, stale) = Preview(workspace, chunk);
            var file = within.Length > 0 ? chunk.File[(within.Length + 1)..] : chunk.File;
            hits.Add(new CodeHit(file, chunk.StartLine, chunk.EndLine, chunk.Name, Math.Round(score, 3), preview, stale));
        }
        hits.Reverse();   // best first
        return hits;
    }

    /// <summary>The first lines of a chunk as they are on disk now, and whether the file has changed since it was indexed.</summary>
    private static (string Preview, bool Stale) Preview(CodeWorkspace workspace, CodeChunk chunk)
    {
        try
        {
            var lines = workspace.ReadLines(chunk.File);
            var stale = CodeChunker.HashOf(lines) != chunk.FileHash;
            var text = string.Join('\n', lines.Skip(chunk.StartLine - 1).Take(Math.Min(8, chunk.EndLine - chunk.StartLine + 1)).Select(l => l.TrimEnd('\r')));
            return (SecretMasker.MaskText(text.Length <= 600 ? text : text[..600]), stale);
        }
        catch (AgentToolException)
        {
            return ("(the file is no longer there)", true);
        }
    }

    private async Task<Loaded> LoadAsync(CodeIndex index, CancellationToken ct)
    {
        if (_cache.TryGetValue(index.Id, out var cached) && cached.UpdatedAt == index.UpdatedAt) return cached;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        var chunks = await db.CodeChunks.AsNoTracking().Where(c => c.IndexId == index.Id).ToArrayAsync(ct);
        var loaded = new Loaded(index.UpdatedAt, chunks, chunks.Select(c => Vectors.FromBytes(c.Embedding)).ToArray());
        // A few targets at a time are searched; older ones are loaded again when needed.
        if (_cache.Count >= 4) _cache.Clear();
        _cache[index.Id] = loaded;
        return loaded;
    }

    /// <summary>The search as a tool for an agent working on this target, or null when the target has no index.</summary>
    public async Task<IAgentTool?> ToolForAsync(string userId, string targetPath, CancellationToken ct) =>
        await FindAsync(userId, targetPath, ct) is { } index ? new SearchCodeTool(this, index, targetPath) : null;

    public const string ToolName = "searchCode";

    /// <summary>One sentence for an agent's task, telling it the tool is there and what it is for.</summary>
    public const string ToolHint =
        "A semantic index of this code exists: searchCode finds code by what it does (\"where are passwords checked\", \"code that builds SQL from input\"), " +
        "also when you do not know the words it uses. Use it to find where to look, then read the code — a hit is a lead, not a finding.";

    private sealed class SearchCodeTool(CodeSearch search, CodeIndex index, string targetPath) : IAgentTool
    {
        public ToolDefinition Definition { get; } = CodeTools.Define(ToolName,
            "Search the code under review by meaning: describe what the code you are looking for does. Returns the closest pieces of code with file and lines. " +
            "For an exact name or string, grep is better.",
            """{"type":"object","properties":{"query":{"type":"string","description":"What the code does, in plain words"},"limit":{"type":"number","description":"How many results (default 8, max 20)"}},"required":["query"],"additionalProperties":false}""");

        public async Task<string> RunAsync(JsonElement input, CancellationToken ct)
        {
            var query = CodeTools.Text(input, "query")?.Trim();
            if (string.IsNullOrEmpty(query)) throw new AgentToolException("query is required.");
            if (query.Length > 500) query = query[..500];

            IReadOnlyList<CodeHit> hits;
            try
            {
                hits = await search.SearchAsync(index, targetPath, query, Math.Clamp(CodeTools.Number(input, "limit") ?? 8, 1, 20), ct);
            }
            catch (InvalidOperationException ex)
            {
                throw new AgentToolException($"The semantic search is not available right now ({ex.Message}). Use grep instead.");
            }
            if (hits.Count == 0) return "Nothing found.";

            var text = new StringBuilder();
            text.AppendLine($"{hits.Count} closest piece(s) of code, best first. Closeness is by meaning and can mislead: read the code before relying on a hit.");
            foreach (var hit in hits)
            {
                text.AppendLine().AppendLine($"{hit.File}:{hit.StartLine}-{hit.EndLine}{(hit.Name.Length > 0 ? $"  {hit.Name}" : "")}  (score {hit.Score:0.00}){(hit.Stale ? "  [the file changed since it was indexed]" : "")}");
                foreach (var line in hit.Preview.Split('\n').Take(6)) text.AppendLine($"    {line}");
            }
            return text.ToString();
        }
    }
}

public sealed record CodeSearchRequest(string? Path, string? Query, int? Limit);

public static class CodeSearchEndpoints
{
    public static void MapCodeSearchEndpoints(this WebApplication app)
    {
        var indexes = app.MapGroup("/code-index").AddEndpointFilter<RequireAuthFilter>();
        indexes.MapGet("/", List);
        indexes.MapDelete("/{id}", Delete);
        app.MapPost("/code-search", Search).AddEndpointFilter<RequireAuthFilter>();
    }

    private static async Task<IResult> List(HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var indexes = await db.CodeIndexes.AsNoTracking().Where(i => i.UserId == userId).OrderByDescending(i => i.UpdatedAt).ToListAsync(ct);
        return Results.Json(indexes.Select(Describe));
    }

    private static async Task<IResult> Delete(string id, HttpContext http, CerberusDbContext db, CodeSearch search, CancellationToken ct)
    {
        var userId = UserId(http);
        var index = await db.CodeIndexes.FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct);
        if (index is null) return Results.Json(new { error = "Index not found" }, statusCode: 404);
        await db.CodeChunks.Where(c => c.IndexId == index.Id).ExecuteDeleteAsync(ct);
        db.CodeIndexes.Remove(index);
        await db.SaveChangesAsync(ct);
        search.Forget(index.Id);
        return Results.NoContent();
    }

    /// <summary>Searches the index that covers <c>path</c> for code that does what <c>query</c> says.</summary>
    private static async Task<IResult> Search(CodeSearchRequest body, HttpContext http, CodeSearch search, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(body.Path) ? "." : body.Path;
        var query = body.Query?.Trim();
        if (string.IsNullOrEmpty(query)) return Results.Json(new { error = "query is required" }, statusCode: 400);
        if (query.Length > 500) return Results.Json(new { error = "query must be at most 500 characters" }, statusCode: 400);
        if (ScopePolicy.Normalize(TargetType.Path, path) is null) return Results.Json(new { error = "path must be relative to the scan root, without '..'" }, statusCode: 400);

        var index = await search.FindAsync(UserId(http), path, ct);
        if (index is null)
        {
            return Results.Json(new { error = $"There is no semantic index covering \"{path}\". Build one with a job of type \"index\"." }, statusCode: 404);
        }
        try
        {
            var hits = await search.SearchAsync(index, path, query, body.Limit ?? 8, ct);
            return Results.Json(new { index = Describe(index), hits });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 502);
        }
    }

    public static object Describe(CodeIndex i) => new
    {
        id = i.Id,
        targetPath = i.TargetPath.Length == 0 ? "." : i.TargetPath,
        engagementId = i.EngagementId,
        model = i.Model,
        dimensions = i.Dimensions,
        fileCount = i.FileCount,
        chunkCount = i.ChunkCount,
        updatedAt = i.UpdatedAt,
    };

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;
}
