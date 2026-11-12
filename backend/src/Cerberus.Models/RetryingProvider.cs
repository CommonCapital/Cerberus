using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Cerberus.Core.Models;

namespace Cerberus.Models;

/// <summary>
/// How a failed call to a model provider is retried: how often, and how long
/// to wait in between. <paramref name="Delay"/> does the waiting, so tests can
/// make it instant.
/// </summary>
public sealed record RetryPolicy(
    int MaxRetries, TimeSpan BaseDelay, TimeSpan MaxDelay, Func<TimeSpan, CancellationToken, Task> Delay)
{
    public static RetryPolicy Standard { get; } = new(
        MaxRetries: 5, BaseDelay: TimeSpan.FromSeconds(2), MaxDelay: TimeSpan.FromSeconds(30),
        Delay: (wait, ct) => Task.Delay(wait, ct));

    /// <summary>The key under which a provider leaves the server's <c>Retry-After</c> on the exception's <c>Data</c>.</summary>
    public const string RetryAfterKey = "RetryAfter";

    /// <summary>The wait before retry number <paramref name="attempt"/> (1-based): doubling, capped, or what the provider asked for.</summary>
    public TimeSpan DelayBefore(int attempt, TimeSpan? retryAfter)
    {
        var backoff = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, BaseDelay.Ticks * (1L << Math.Min(attempt - 1, 20))));
        return retryAfter is { } asked && asked > backoff ? asked : backoff;
    }
}

/// <summary>
/// Wraps a provider so that a call which fails for a passing reason — the
/// provider is overloaded or rate-limiting, the network dropped — is tried
/// again after a wait, instead of ending the reply. Only failures before the
/// first event are retried: once output has started, repeating the call would
/// repeat the output. Each retry is announced in the stream so the operator
/// sees why nothing is happening.
/// </summary>
public sealed class RetryingProvider(IModelProvider inner, RetryPolicy policy) : IModelProvider
{
    public ModelProvider Provider => inner.Provider;

    public IModelProvider Inner => inner;

    public IAsyncEnumerable<string> StreamAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default) =>
        WithRetry(() => inner.StreamAsync(request, cancellationToken), announce: null, cancellationToken);

    public IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        WithRetry(() => inner.StreamChatAsync(request, cancellationToken),
            (attempt, wait, reason) => new RetryScheduled(attempt, policy.MaxRetries, wait, reason), cancellationToken);

    private async IAsyncEnumerable<T> WithRetry<T>(
        Func<IAsyncEnumerable<T>> start, Func<int, TimeSpan, string, T>? announce, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var started = false;
            Exception? failure = null;
            await using var stream = start().GetAsyncEnumerator(ct);
            while (true)
            {
                bool has;
                try
                {
                    has = await stream.MoveNextAsync();
                }
                catch (Exception ex) when (!started && attempt < policy.MaxRetries && Describe(ex, ct) is not null)
                {
                    failure = ex;
                    break;
                }
                if (!has) yield break;
                started = true;
                yield return stream.Current;
            }

            var retryAfter = RetryAfterOf(failure);
            // A provider that wants us gone for longer than we are willing to wait is not retried: say so instead.
            if (retryAfter > policy.MaxDelay + policy.MaxDelay)
            {
                throw new InvalidOperationException(
                    $"The model provider is rate-limiting and asked to wait {Math.Ceiling(retryAfter.Value.TotalSeconds)} s. Try again later.", failure);
            }
            var wait = policy.DelayBefore(attempt + 1, retryAfter);
            if (announce is not null) yield return announce(attempt + 1, wait, Describe(failure, ct)!);
            await policy.Delay(wait, ct);
        }
    }

    /// <summary>Why this failure is worth another try, in a few words — or null when it is not (a bad request, a missing key, our own cancellation).</summary>
    public static string? Describe(Exception exception, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;
        string? general = null;   // "the connection failed", unless something further in says more precisely why
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (StatusOf(ex) is { } status)
            {
                return status switch
                {
                    429 => "rate limited",
                    408 => "request timed out",
                    529 => "provider overloaded",
                    >= 500 and <= 599 => $"provider error {status}",
                    _ => null,   // 4xx: the request itself is wrong; repeating it changes nothing
                };
            }
            if (ex is SocketException or IOException) return "connection lost";
            // HttpClient reports its own timeout as a cancellation that is not ours.
            if (ex is TaskCanceledException or TimeoutException) return "request timed out";
            if (ex is HttpRequestException) general ??= "connection failed";
            var name = ex.GetType().Name;
            if (name.Contains("5xx", StringComparison.Ordinal) || name.Contains("RateLimit", StringComparison.Ordinal) ||
                name.Contains("Overloaded", StringComparison.Ordinal))
            {
                return "provider busy";
            }
        }
        return general;
    }

    /// <summary>The HTTP status an exception carries, whichever library threw it.</summary>
    private static int? StatusOf(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: { } code }) return (int)code;
        return ex.GetType().GetProperty("StatusCode")?.GetValue(ex) switch
        {
            HttpStatusCode status => (int)status,
            int status => status,
            _ => null,
        };
    }

    private static TimeSpan? RetryAfterOf(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex.Data[RetryPolicy.RetryAfterKey] is TimeSpan wait && wait > TimeSpan.Zero) return wait;
        }
        return null;
    }
}
