using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerberus.Api.Chat;

/// <param name="DependsOn">Ids of the tasks that must be completed before this one can start.</param>
public sealed record ChatTask(string Id, string Content, string Status, IReadOnlyList<string> DependsOn);

/// <summary>
/// The chat agent's task list (PLAN.md §6.5 "visible task list", §19, §20): a
/// plan the model keeps for work of several steps, visible to the operator as
/// it changes. The model rewrites the whole list with every update; the server
/// checks it, stores it with the session and reminds the model of it on every
/// request — so it survives a compacted conversation. Tasks may depend on one
/// another; the server works out which can be started now.
/// </summary>
public sealed class TaskList
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    public const int MaxTasks = 30;
    public const int MaxContent = 200;

    private static readonly string[] Statuses = [Pending, InProgress, Completed, Cancelled];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public IReadOnlyList<ChatTask> Tasks { get; private set; }

    /// <summary>True once the model has changed the list in this reply.</summary>
    public bool Changed { get; private set; }

    public TaskList(string? stored) => Tasks = Read(stored);

    public static IReadOnlyList<ChatTask> Read(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ChatTask>>(stored, Json)?.Where(t => t.Id is not null && t.Content is not null).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public string Serialize() => JsonSerializer.Serialize(Tasks, Json);

    /// <summary>Tasks that can be started now: pending, with everything they depend on completed.</summary>
    public static IReadOnlyList<ChatTask> Runnable(IReadOnlyList<ChatTask> tasks)
    {
        var done = tasks.Where(t => t.Status == Completed).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        return tasks.Where(t => t.Status == Pending && t.DependsOn.All(done.Contains)).ToList();
    }

    /// <summary>
    /// Replaces the list with what the model sent. Throws <see cref="ArgumentException"/>
    /// with a message for the model when the list does not hold together.
    /// Returns the tool's result: the list as stored, what can be started next, and where it stands.
    /// </summary>
    public string Update(JsonElement input)
    {
        if (!input.TryGetProperty("tasks", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("tasks is required: the whole list, every time.");
        }
        if (items.GetArrayLength() > MaxTasks) throw new ArgumentException($"At most {MaxTasks} tasks. Group smaller steps together.");

        var tasks = new List<ChatTask>();
        var position = 0;
        foreach (var item in items.EnumerateArray())
        {
            position++;
            if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Task {position} is not an object.");
            var content = Text(item, "content")?.Trim();
            if (string.IsNullOrEmpty(content)) throw new ArgumentException($"Task {position} has no content.");
            if (content.Length > MaxContent) content = content[..MaxContent];

            var id = Text(item, "id")?.Trim();
            if (string.IsNullOrEmpty(id)) id = position.ToString();
            if (id.Length > 40) throw new ArgumentException($"Task {position}: the id is too long; use a short one such as \"{position}\".");
            if (tasks.Any(t => t.Id == id)) throw new ArgumentException($"Two tasks have the id \"{id}\"; ids must be unique.");

            var status = (Text(item, "status") ?? Pending).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            if (!Statuses.Contains(status)) throw new ArgumentException($"Task \"{id}\": status must be one of {string.Join(", ", Statuses)}.");

            var dependsOn = new List<string>();
            if (item.TryGetProperty("dependsOn", out var deps) && deps.ValueKind == JsonValueKind.Array)
            {
                foreach (var dep in deps.EnumerateArray())
                {
                    var name = dep.ValueKind == JsonValueKind.String ? dep.GetString()!.Trim() : dep.ToString();
                    if (name.Length > 0 && !dependsOn.Contains(name)) dependsOn.Add(name);
                }
            }
            tasks.Add(new ChatTask(id, content, status, dependsOn));
        }

        var ids = tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (task.DependsOn.FirstOrDefault(d => !ids.Contains(d)) is { } missing)
            {
                throw new ArgumentException($"Task \"{task.Id}\" depends on \"{missing}\", which is not in the list.");
            }
            if (task.DependsOn.Contains(task.Id)) throw new ArgumentException($"Task \"{task.Id}\" depends on itself.");
        }
        if (FindCycle(tasks) is { } cycle) throw new ArgumentException($"The tasks depend on each other in a circle ({cycle}); none of them could ever start.");

        // Work that is under way or done must stand on finished ground.
        var done = tasks.Where(t => t.Status == Completed).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        if (tasks.FirstOrDefault(t => t.Status is InProgress or Completed && t.DependsOn.Any(d => !done.Contains(d))) is { } early)
        {
            throw new ArgumentException(
                $"Task \"{early.Id}\" is {early.Status.Replace('_', ' ')} but depends on \"{early.DependsOn.First(d => !done.Contains(d))}\", which is not completed. Finish that first, or drop the dependency.");
        }

        Tasks = tasks;
        Changed = true;

        var next = Runnable(tasks);
        var open = tasks.Count(t => t.Status is Pending or InProgress);
        return JsonSerializer.Serialize(new
        {
            tasks,
            next = next.Select(t => t.Id),
            summary = tasks.Count == 0
                ? "The list is empty."
                : $"{tasks.Count(t => t.Status == Completed)} of {tasks.Count} completed" +
                  (tasks.Count(t => t.Status == InProgress) is > 1 and var many ? $"; {many} are in progress at once — work on one at a time" : "") +
                  (open == 0 ? "; nothing is left to do." : next.Count > 0 ? $"; can start now: {string.Join(", ", next.Select(t => t.Id))}." : "."),
        }, Json);
    }

    /// <summary>A circle in the dependencies, as "a → b → a", or null when there is none.</summary>
    private static string? FindCycle(IReadOnlyList<ChatTask> tasks)
    {
        var byId = tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 1 = on the current path, 2 = cleared
        var path = new List<string>();

        string? Visit(string id)
        {
            if (state.TryGetValue(id, out var s))
            {
                return s == 1 ? string.Join(" → ", path.Skip(path.IndexOf(id)).Append(id)) : null;
            }
            state[id] = 1;
            path.Add(id);
            foreach (var dep in byId[id].DependsOn)
            {
                if (Visit(dep) is { } cycle) return cycle;
            }
            path.RemoveAt(path.Count - 1);
            state[id] = 2;
            return null;
        }

        foreach (var task in tasks)
        {
            if (Visit(task.Id) is { } cycle) return cycle;
        }
        return null;
    }

    /// <summary>The list as a section of the system prompt; empty when there is no list.</summary>
    public static string PromptSection(IReadOnlyList<ChatTask> tasks)
    {
        if (tasks.Count == 0) return "";
        var text = new StringBuilder();
        text.AppendLine("## Your task list");
        text.AppendLine("As you last set it with updateTasks. The user sees it. Keep it true: update it when a task starts, ends or no longer applies.");
        foreach (var task in tasks)
        {
            var mark = task.Status switch { Completed => "[x]", InProgress => "[>]", Cancelled => "[-]", _ => "[ ]" };
            var after = task.DependsOn.Count > 0 ? $" (after {string.Join(", ", task.DependsOn)})" : "";
            text.AppendLine($"- {mark} {task.Id}: {task.Content.Replace('\n', ' ')}{after}");
        }
        var next = Runnable(tasks);
        if (next.Count > 0 && tasks.All(t => t.Status != InProgress)) text.AppendLine($"Can start now: {string.Join(", ", next.Select(t => t.Id))}.");
        return text.ToString().TrimEnd();
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.ToString() : null : null;
}
