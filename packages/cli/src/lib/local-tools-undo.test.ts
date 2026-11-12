import { afterAll, expect, test } from "bun:test";
import { mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { executeLocalTool } from "./local-tools";
import { UndoJournal } from "./undo-journal";

// The agent's write and edit tools, run for real in a scratch project, with the journal attached as the chat attaches it.

const project = realpathSync(mkdtempSync(join(tmpdir(), "nightcode-tools-undo-")));
const journalDir = mkdtempSync(join(tmpdir(), "nightcode-journal-"));
afterAll(() => {
  rmSync(project, { recursive: true, force: true });
  rmSync(journalDir, { recursive: true, force: true });
});

test("what the write and edit tools change can be undone and redone as one turn", async () => {
  const previous = process.cwd();
  process.chdir(project);
  try {
    writeFileSync("app.py", "def run():\n    return 1\n");
    const journal = new UndoJournal(journalDir);
    const changes: string[] = [];
    const record = (change: { path: string; before: string | null; after: string }) => {
      changes.push(`${change.path}:${change.before === null ? "new" : "changed"}`);
      journal.record("turn-1", process.cwd(), change);
    };

    await executeLocalTool("editFile", { path: "app.py", oldString: "return 1", newString: "return 2" }, "BUILD", record);
    await executeLocalTool("writeFile", { path: "notes/plan.md", content: "# Plan\n" }, "BUILD", record);
    await executeLocalTool("readFile", { path: "app.py" }, "BUILD", record);   // reading changes nothing
    expect(changes).toEqual(["app.py:changed", "notes/plan.md:new"]);
    expect(readFileSync("app.py", "utf-8")).toContain("return 2");

    expect(journal.undo(process.cwd())).toEqual({ restored: ["notes/plan.md", "app.py"], skipped: [] });
    expect(readFileSync("app.py", "utf-8")).toBe("def run():\n    return 1\n");
    expect(() => readFileSync("notes/plan.md", "utf-8")).toThrow();

    expect(journal.redo(process.cwd())!.restored).toEqual(["app.py", "notes/plan.md"]);
    expect(readFileSync("app.py", "utf-8")).toContain("return 2");
    expect(readFileSync("notes/plan.md", "utf-8")).toBe("# Plan\n");
  } finally {
    process.chdir(previous);
  }
});

test("a failed edit records nothing", async () => {
  const previous = process.cwd();
  process.chdir(project);
  try {
    writeFileSync("b.py", "x = 1\n");
    const changes: unknown[] = [];
    await expect(executeLocalTool("editFile", { path: "b.py", oldString: "not there", newString: "y" }, "BUILD", (c) => changes.push(c))).rejects.toThrow("not found");
    expect(changes).toEqual([]);
  } finally {
    process.chdir(previous);
  }
});
