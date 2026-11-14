using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerberus.Api.Audit;
using Cerberus.Api.Auth;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Jobs;

public sealed record AnswerRequest(List<string>? Selected, string? Text);

public static class QuestionStatus
{
    public const string Pending = "Pending";
    public const string Answered = "Answered";

    /// <summary>Nobody answered in time; the run fell back to its safe default.</summary>
    public const string Expired = "Expired";

    /// <summary>The job ended (cancelled, timed out, server restart) before an answer came.</summary>
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// Where running jobs put questions to the operator and wait (PLAN.md §6.5).
/// The question is stored and announced on the run's stream; the job moves to
/// AwaitingInput with its time limit paused; the answer arrives through
/// <c>POST /questions/{id}/answer</c> and wakes the job up.
/// </summary>
public sealed class OperatorDesk(
    IServiceScopeFactory scopes, JobEvents events, RunStream stream, AuditLog audit, JobOptions options)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ConcurrentDictionary<string, TaskCompletionSource<OperatorAnswer>> _waiting = new();

    public async Task<OperatorAnswer?> AskAsync(
        Job job, JobTimeLimit timeLimit, OperatorQuestionSpec spec, CancellationToken ct)
    {
        if (OperatorQuestions.ValidateSpec(spec) is { } problem) throw new ArgumentException(problem, nameof(spec));
        ct.ThrowIfCancellationRequested();

        var question = new OperatorQuestion
        {
            UserId = job.UserId,
            JobId = job.Id,
            EngagementId = job.EngagementId,
            Text = spec.Text.Trim(),
            Options = JsonSerializer.Serialize(spec.Options, JsonOptions),
            MultiSelect = spec.MultiSelect,
            AllowText = spec.AllowText,
            ExpiresAt = DateTime.UtcNow + options.QuestionTimeout,
        };
        var waiter = new TaskCompletionSource<OperatorAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[question.Id] = waiter;

        try
        {
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
                db.OperatorQuestions.Add(question);
                await db.SaveChangesAsync(CancellationToken.None);
                await db.Jobs
                    .Where(j => j.Id == job.Id && j.State == nameof(JobState.Running))
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, nameof(JobState.AwaitingInput)), CancellationToken.None);
            }
            await audit.AppendAsync(job.UserId, "question.ask", AuditOutcome.Ok,
                $"Job {job.Id} asked: {question.Text}", job.EngagementId, job.TargetPath);
            await events.PublishAsync(job.Id);
            await stream.EmitAsync(job.Id, job.UserId, RunEventTypes.Question, Describe(question));

            OperatorAnswer? answer;
            // Waiting for a person does not count against the job's time limit.
            using (timeLimit.Suspend())
            {
                try
                {
                    answer = await waiter.Task.WaitAsync(options.QuestionTimeout, ct);
                }
                catch (TimeoutException)
                {
                    // An answer that lands at this very moment still wins: only a pending question expires.
                    answer = await CloseAsync(question.Id, QuestionStatus.Expired)
                        ? null
                        : await waiter.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    await CloseAsync(question.Id, QuestionStatus.Cancelled);
                    throw;   // the job is being stopped; it does not go back to Running
                }
            }

            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
                await db.Jobs
                    .Where(j => j.Id == job.Id && j.State == nameof(JobState.AwaitingInput))
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, nameof(JobState.Running)), CancellationToken.None);
            }
            await events.PublishAsync(job.Id);
            return answer;
        }
        finally
        {
            _waiting.TryRemove(question.Id, out _);
        }
    }

    /// <summary>Hands an answer to the job that is waiting for it. False if no job is waiting in this process.</summary>
    public bool Deliver(string questionId, OperatorAnswer answer) =>
        _waiting.TryGetValue(questionId, out var waiter) && waiter.TrySetResult(answer);

    /// <summary>Closes a still-pending question and says so on the stream. False if it was no longer pending.</summary>
    private async Task<bool> CloseAsync(string questionId, string status)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        var now = DateTime.UtcNow;
        var closed = await db.OperatorQuestions
            .Where(q => q.Id == questionId && q.Status == QuestionStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, status)
                .SetProperty(q => q.ResolvedAt, now));
        if (closed != 1) return false;

        var question = await db.OperatorQuestions.AsNoTracking().FirstAsync(q => q.Id == questionId);
        await stream.EmitAsync(question.JobId, question.UserId, RunEventTypes.QuestionResolved, Describe(question));
        return true;
    }

    public static OperatorQuestionSpec SpecOf(OperatorQuestion q) => new(
        q.Text,
        JsonSerializer.Deserialize<List<QuestionOption>>(q.Options, JsonOptions) ?? [],
        q.MultiSelect,
        q.AllowText);

    internal static object Describe(OperatorQuestion q) => new
    {
        q.Id, q.JobId, q.EngagementId, q.Text,
        options = JsonSerializer.Deserialize<JsonElement>(q.Options),
        q.MultiSelect, q.AllowText, q.Status,
        answer = q.Answer is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(q.Answer),
        q.CreatedAt, q.ExpiresAt, q.ResolvedAt,
    };
}

public static class QuestionEndpoints
{
    public static void MapQuestionEndpoints(this WebApplication app)
    {
        var questions = app.MapGroup("/questions").AddEndpointFilter<RequireAuthFilter>();
        questions.MapGet("/", List);
        questions.MapGet("/{id}", Get);
        questions.MapPost("/{id}/answer", Answer);
    }

    private static async Task<IResult> List(
        HttpContext http, CerberusDbContext db, string? status, string? jobId, int? limit, CancellationToken ct)
    {
        var userId = UserId(http);
        var query = db.OperatorQuestions.AsNoTracking().Where(q => q.UserId == userId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var wanted = new[] { QuestionStatus.Pending, QuestionStatus.Answered, QuestionStatus.Expired, QuestionStatus.Cancelled }
                .FirstOrDefault(s => s.Equals(status, StringComparison.OrdinalIgnoreCase));
            if (wanted is null) return Results.Json(new { error = "status must be pending, answered, expired or cancelled" }, statusCode: 400);
            query = query.Where(q => q.Status == wanted);
        }
        if (!string.IsNullOrWhiteSpace(jobId)) query = query.Where(q => q.JobId == jobId);

        var items = await query.OrderByDescending(q => q.CreatedAt).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync(ct);
        return Results.Json(items.Select(OperatorDesk.Describe), OperatorDesk.JsonOptions);
    }

    private static async Task<IResult> Get(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var question = await db.OperatorQuestions.AsNoTracking().FirstOrDefaultAsync(q => q.Id == id && q.UserId == userId, ct);
        return question is null ? NotFound() : Results.Json(OperatorDesk.Describe(question), OperatorDesk.JsonOptions);
    }

    private static async Task<IResult> Answer(
        string id, AnswerRequest body, HttpContext http, CerberusDbContext db, OperatorDesk desk,
        RunStream stream, AuditLog audit, Cluster.ISignals signals, CancellationToken ct)
    {
        var userId = UserId(http);
        var question = await db.OperatorQuestions.AsNoTracking().FirstOrDefaultAsync(q => q.Id == id && q.UserId == userId, ct);
        if (question is null) return NotFound();
        if (question.Status != QuestionStatus.Pending)
        {
            return Results.Json(new { error = $"This question is already {question.Status.ToLowerInvariant()}" }, statusCode: 409);
        }

        var (answer, error) = OperatorQuestions.ValidateAnswer(OperatorDesk.SpecOf(question), body.Selected, body.Text);
        if (answer is null) return Results.Json(new { error }, statusCode: 400);

        // Answer exactly once: only a still-pending question can be answered.
        var now = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(answer, OperatorDesk.JsonOptions);
        var answered = await db.OperatorQuestions
            .Where(q => q.Id == id && q.Status == QuestionStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, QuestionStatus.Answered)
                .SetProperty(q => q.Answer, json)
                .SetProperty(q => q.ResolvedAt, now), ct);
        if (answered != 1)
        {
            return Results.Json(new { error = "This question is no longer waiting for an answer" }, statusCode: 409);
        }

        var current = await db.OperatorQuestions.AsNoTracking().FirstAsync(q => q.Id == id, ct);
        var summary = string.Join(", ", answer.Selected) + (answer.Text is null ? "" : (answer.Selected.Count > 0 ? " — " : "") + answer.Text);
        await audit.AppendAsync(userId, "question.answer", AuditOutcome.Ok,
            $"Answered \"{question.Text}\": {summary}", question.EngagementId, ct: ct);
        await stream.EmitAsync(current.JobId, current.UserId, RunEventTypes.QuestionResolved, OperatorDesk.Describe(current));
        // Wakes the waiting job — here, or in whichever instance of the service it is waiting.
        if (!desk.Deliver(id, answer) && signals.Distributed)
        {
            await signals.SendAsync(Cluster.Topics.Answer, JsonSerializer.Serialize(
                new Cluster.SignalRouter.AnswerSignal(id, [.. answer.Selected], answer.Text), Cluster.SignalRouter.Json));
        }

        return Results.Json(OperatorDesk.Describe(current), OperatorDesk.JsonOptions);
    }

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;

    private static IResult NotFound() => Results.Json(new { error = "Question not found" }, statusCode: 404);
}
