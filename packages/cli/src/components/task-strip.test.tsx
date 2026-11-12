import { expect, test } from "bun:test";
import { testRender } from "@opentui/react/test-utils";
import { TaskStrip } from "./task-strip";
import { ThemeProvider } from "../providers/theme";
import type { ChatTask } from "../lib/task-list";

const task = (id: string, content: string, status: ChatTask["status"], dependsOn: string[] = []): ChatTask => ({ id, content, status, dependsOn });

async function frame(tasks: ChatTask[] | null) {
  const { renderOnce, captureCharFrame, renderer } = await testRender(
    <ThemeProvider>
      <box flexDirection="column">
        <text>CHAT</text>
        <TaskStrip tasks={tasks} />
      </box>
    </ThemeProvider>,
    { width: 80, height: 14 },
  );
  await renderOnce();
  const text = captureCharFrame();
  renderer.destroy();
  return text;
}

test("the list shows what is done, what is under way and what waits for what", async () => {
  const text = await frame([
    task("1", "Read the code", "completed"),
    task("2", "Fix the login check", "in_progress", ["1"]),
    task("3", "Add a test", "pending", ["2"]),
    task("4", "Update the changelog", "cancelled"),
  ]);

  expect(text).toContain("Tasks 1/3");
  expect(text).toContain("✔ Read the code");
  expect(text).toContain("▶ Fix the login check");
  expect(text).toContain("○ Add a test after 2");
  expect(text).toContain("– Update the changelog (dropped)");
});

test("a finished list shrinks to one line, and no list shows nothing", async () => {
  const done = await frame([task("1", "Read", "completed"), task("2", "Fix", "completed")]);
  expect(done).toContain("Tasks: all 2 done");
  expect(done).not.toContain("Read");

  expect(await frame(null)).not.toContain("Tasks");
  expect(await frame([])).not.toContain("Tasks");
});

test("a long list says how many tasks are not shown", async () => {
  const many = Array.from({ length: 12 }, (_, i) => task(`${i + 1}`, `Step ${i + 1}`, i === 0 ? "in_progress" : "pending"));
  const text = await frame(many);

  expect(text).toContain("Tasks 0/12");
  expect(text).toContain("▶ Step 1");
  expect(text).toContain("… 5 more");
});
