import { describe, expect, test } from "bun:test";
import { latestTasks, taskProgress, visibleTasks, type ChatTask } from "./task-list";
import { summarizeToolCall, toolResultLines } from "./tool-display";

const task = (id: string, status: ChatTask["status"], dependsOn: string[] = []): ChatTask => ({ id, content: `Do ${id}`, status, dependsOn });

const update = (tasks: unknown, state = "output-available") => ({ type: "tool-updateTasks", state, output: { tasks, summary: "…" } });

describe("latestTasks", () => {
  test("the current list is the result of the latest update in the conversation", () => {
    const messages = [
      { parts: [{ type: "text" }, update([task("a", "in_progress")])] },
      { parts: [{ type: "text" }] },
      { parts: [update([task("a", "completed"), task("b", "pending", ["a"])]), { type: "text" }] },
    ];

    expect(latestTasks(messages)).toEqual([task("a", "completed"), task("b", "pending", ["a"])]);
  });

  test("an update that failed or has not finished does not count; with none there is no list", () => {
    const messages = [
      { parts: [update([task("a", "pending")])] },
      { parts: [update([task("x", "pending")], "output-error"), { type: "tool-updateTasks", state: "input-available" }] },
    ];

    expect(latestTasks(messages)).toEqual([task("a", "pending")]);
    expect(latestTasks([{ parts: [{ type: "text" }] }])).toBeNull();
    expect(latestTasks([])).toBeNull();
  });

  test("entries that are not tasks are left out rather than shown wrongly", () => {
    const messages = [{ parts: [update([task("a", "pending"), { id: "b" }, "nonsense", { id: "c", content: "x", status: "weird" }])] }];
    expect(latestTasks(messages)).toEqual([task("a", "pending")]);
  });
});

describe("taskProgress", () => {
  test("counts what is done against what is to be done, leaving out what was dropped", () => {
    expect(taskProgress([task("a", "completed"), task("b", "in_progress"), task("c", "pending"), task("d", "cancelled")])).toEqual({ done: 1, total: 3, open: 2 });
    expect(taskProgress([task("a", "completed"), task("b", "cancelled")])).toEqual({ done: 1, total: 1, open: 0 });
  });
});

describe("visibleTasks", () => {
  test("a short list is shown whole, in its own order", () => {
    const tasks = [task("a", "completed"), task("b", "in_progress"), task("c", "pending")];
    expect(visibleTasks(tasks, 7)).toEqual({ shown: tasks, hidden: 0 });
  });

  test("a long list keeps what is under way and what comes next, then the latest finished", () => {
    const tasks = [
      task("d1", "completed"), task("d2", "completed"), task("d3", "completed"),
      task("now", "in_progress"), task("p1", "pending"), task("p2", "pending"), task("p3", "pending"),
    ];

    const { shown, hidden } = visibleTasks(tasks, 4);

    expect(shown.map((t) => t.id)).toEqual(["now", "p1", "p2", "p3"]);   // in list order
    expect(hidden).toBe(3);
    expect(visibleTasks(tasks, 5).shown.map((t) => t.id)).toEqual(["d3", "now", "p1", "p2", "p3"]);   // the most recent finished one
  });
});

describe("how an update shows in the conversation", () => {
  test("the call says how many tasks, the result where the list stands", () => {
    expect(summarizeToolCall("updateTasks", { tasks: [1, 2, 3] })).toBe("3 tasks");
    expect(summarizeToolCall("updateTasks", { tasks: [1] })).toBe("1 task");
    expect(toolResultLines("updateTasks", { tasks: [], summary: "1 of 3 completed; can start now: 2." })).toEqual([
      { text: "1 of 3 completed; can start now: 2.", tone: "plain" },
    ]);
  });
});
