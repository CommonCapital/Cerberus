// The chat agent's task list, as the CLI shows it. The list lives in the
// conversation: each update is a tool call whose result carries the whole
// list, so the latest such result is the current list.

export type TaskStatus = "pending" | "in_progress" | "completed" | "cancelled";

export type ChatTask = { id: string; content: string; status: TaskStatus; dependsOn: string[] };

type Part = { type: string; state?: string; output?: unknown };

const STATUSES: TaskStatus[] = ["pending", "in_progress", "completed", "cancelled"];

function readTasks(output: unknown): ChatTask[] | null {
  const tasks = output && typeof output === "object" ? (output as { tasks?: unknown }).tasks : undefined;
  if (!Array.isArray(tasks)) return null;
  return tasks
    .filter((t): t is Record<string, unknown> => !!t && typeof t === "object")
    .filter((t) => typeof t.id === "string" && typeof t.content === "string" && STATUSES.includes(t.status as TaskStatus))
    .map((t) => ({
      id: t.id as string,
      content: t.content as string,
      status: t.status as TaskStatus,
      dependsOn: Array.isArray(t.dependsOn) ? t.dependsOn.filter((d): d is string => typeof d === "string") : [],
    }));
}

/** The task list as the agent last set it, or null if it never made one. */
export function latestTasks(messages: readonly { parts: readonly Part[] }[]): ChatTask[] | null {
  for (let m = messages.length - 1; m >= 0; m--) {
    const parts = messages[m]!.parts;
    for (let p = parts.length - 1; p >= 0; p--) {
      const part = parts[p]!;
      if (part.type === "tool-updateTasks" && part.state === "output-available") {
        const tasks = readTasks(part.output);
        if (tasks) return tasks;
      }
    }
  }
  return null;
}

export const TASK_MARK: Record<TaskStatus, string> = { pending: "○", in_progress: "▶", completed: "✔", cancelled: "–" };

/** "2 of 5 done", not counting what was cancelled. */
export function taskProgress(tasks: readonly ChatTask[]): { done: number; total: number; open: number } {
  const counted = tasks.filter((t) => t.status !== "cancelled");
  const done = counted.filter((t) => t.status === "completed").length;
  return { done, total: counted.length, open: counted.length - done };
}

/**
 * The tasks to show when there is room for only `limit` lines: what is under
 * way first, then what comes next, then the most recently finished — with a
 * count of what is left out.
 */
export function visibleTasks(tasks: readonly ChatTask[], limit: number): { shown: ChatTask[]; hidden: number } {
  if (tasks.length <= limit) return { shown: [...tasks], hidden: 0 };
  const rank = (t: ChatTask) => (t.status === "in_progress" ? 0 : t.status === "pending" ? 1 : 2);
  const keep = new Set(
    tasks
      .map((task, index) => ({ task, index }))
      // Among finished tasks the latest are the most telling; among the others the earliest come first.
      .sort((a, b) => rank(a.task) - rank(b.task) || (rank(a.task) === 2 ? b.index - a.index : a.index - b.index))
      .slice(0, limit)
      .map((entry) => entry.index),
  );
  return { shown: tasks.filter((_, index) => keep.has(index)), hidden: tasks.length - keep.size };
}
