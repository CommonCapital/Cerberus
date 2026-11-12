import { afterAll, describe, expect, test } from "bun:test";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describeOutcome, UndoJournal } from "./undo-journal";

const root = mkdtempSync(join(tmpdir(), "nightcode-undo-"));
afterAll(() => rmSync(root, { recursive: true, force: true }));

/** A project directory and a journal, with a helper that makes a change as the agent's tools would. */
function setup() {
  const id = Math.random().toString(36).slice(2, 8);
  const cwd = join(root, `project-${id}`);
  mkdirSync(cwd, { recursive: true });
  const journal = new UndoJournal(join(root, `journal-${id}`));
  const file = (path: string) => join(cwd, path);
  const read = (path: string) => (existsSync(file(path)) ? readFileSync(file(path), "utf-8") : null);
  const agentWrites = (turn: string, path: string, after: string) => {
    const before = read(path);
    mkdirSync(join(file(path), ".."), { recursive: true });
    writeFileSync(file(path), after);
    journal.record(turn, cwd, { path, before, after });
  };
  return { cwd, journal, file, read, agentWrites };
}

describe("undo", () => {
  test("puts back what the last turn replaced, and removes what it created", () => {
    const { cwd, journal, file, read, agentWrites } = setup();
    writeFileSync(file("a.py"), "original\n");
    agentWrites("turn-1", "a.py", "changed\n");
    agentWrites("turn-1", "src/new.py", "brand new\n");

    const outcome = journal.undo(cwd);

    expect(outcome).toEqual({ restored: ["src/new.py", "a.py"], skipped: [] });
    expect(read("a.py")).toBe("original\n");
    expect(read("src/new.py")).toBeNull();
  });

  test("a file edited twice in one turn goes all the way back", () => {
    const { cwd, journal, file, read, agentWrites } = setup();
    writeFileSync(file("a.py"), "v0");
    agentWrites("turn-1", "a.py", "v1");
    agentWrites("turn-1", "a.py", "v2");

    expect(journal.undo(cwd)).toEqual({ restored: ["a.py"], skipped: [] });
    expect(read("a.py")).toBe("v0");
  });

  test("works turn by turn, latest first, and then has nothing left", () => {
    const { cwd, journal, file, read, agentWrites } = setup();
    writeFileSync(file("a.py"), "v0");
    agentWrites("turn-1", "a.py", "v1");
    agentWrites("turn-2", "a.py", "v2");

    journal.undo(cwd);
    expect(read("a.py")).toBe("v1");
    expect(journal.canUndo()).toBe(true);
    journal.undo(cwd);
    expect(read("a.py")).toBe("v0");
    expect(journal.canUndo()).toBe(false);
    expect(journal.undo(cwd)).toBeNull();
  });

  test("a file the operator changed since is left alone and named", () => {
    const { cwd, journal, file, read, agentWrites } = setup();
    writeFileSync(file("a.py"), "original");
    writeFileSync(file("b.py"), "original b");
    agentWrites("turn-1", "a.py", "agent's version");
    agentWrites("turn-1", "b.py", "agent's b");
    writeFileSync(file("a.py"), "my own later edit");

    const outcome = journal.undo(cwd)!;

    expect(outcome.restored).toEqual(["b.py"]);
    expect(outcome.skipped).toEqual([{ path: "a.py", reason: "it has changed since the agent wrote it" }]);
    expect(read("a.py")).toBe("my own later edit");
    expect(read("b.py")).toBe("original b");
  });

  test("changes made in another project directory are not touched from here", () => {
    const { cwd, journal, file, read, agentWrites } = setup();
    writeFileSync(file("a.py"), "original");
    agentWrites("turn-1", "a.py", "changed");

    const elsewhere = join(root, "elsewhere");
    mkdirSync(elsewhere, { recursive: true });
    const outcome = journal.undo(elsewhere)!;

    expect(outcome.restored).toEqual([]);
    expect(outcome.skipped[0]!.reason).toContain("another directory");
    expect(read("a.py")).toBe("changed");
    expect(cwd).not.toBe(elsewhere);
  });

  test("a change too large to keep cannot be undone, and says so", () => {
    const { cwd, journal, file, read, agentWrites } = setup();
    writeFileSync(file("big.txt"), "small");
    agentWrites("turn-1", "big.txt", "x".repeat(1_000_001));

    expect(journal.undo(cwd)!.skipped).toEqual([{ path: "big.txt", reason: "it was too large to keep a copy of" }]);
    expect(read("big.txt")!.length).toBe(1_000_001);
  });
});

describe("redo", () => {
  test("re-applies what was undone, in the order it was undone back", () => {
    const { cwd, journal, file, read, agentWrites } = setup();
    writeFileSync(file("a.py"), "v0");
    agentWrites("turn-1", "a.py", "v1");
    agentWrites("turn-1", "new.py", "created");
    journal.undo(cwd);
    expect(journal.canRedo()).toBe(true);

    expect(journal.redo(cwd)).toEqual({ restored: ["a.py", "new.py"], skipped: [] });
    expect(read("a.py")).toBe("v1");
    expect(read("new.py")).toBe("created");
    expect(journal.canRedo()).toBe(false);
    expect(journal.redo(cwd)).toBeNull();
    // And it can be undone again.
    journal.undo(cwd);
    expect(read("a.py")).toBe("v0");
  });

  test("a new change by the agent after an undo ends the chance to redo", () => {
    const { cwd, journal, file, read, agentWrites } = setup();
    writeFileSync(file("a.py"), "v0");
    agentWrites("turn-1", "a.py", "v1");
    journal.undo(cwd);

    agentWrites("turn-2", "a.py", "another way");

    expect(journal.canRedo()).toBe(false);
    expect(journal.redo(cwd)).toBeNull();
    journal.undo(cwd);
    expect(read("a.py")).toBe("v0");   // only the new turn is there to undo
    expect(journal.canUndo()).toBe(false);
  });

  test("a file changed since the undo is not overwritten by a redo", () => {
    const { cwd, journal, file, read, agentWrites } = setup();
    writeFileSync(file("a.py"), "v0");
    agentWrites("turn-1", "a.py", "v1");
    journal.undo(cwd);
    writeFileSync(file("a.py"), "mine now");

    expect(journal.redo(cwd)!.skipped).toEqual([{ path: "a.py", reason: "it has changed since the undo" }]);
    expect(read("a.py")).toBe("mine now");
  });
});

describe("the journal", () => {
  test("survives a restart of the CLI", () => {
    const { cwd, journal, file, read, agentWrites } = setup();
    writeFileSync(file("a.py"), "v0");
    agentWrites("turn-1", "a.py", "v1");

    const reopened = new UndoJournal((journal as unknown as { directory: string }).directory);
    expect(reopened.canUndo()).toBe(true);
    reopened.undo(cwd);
    expect(read("a.py")).toBe("v0");
  });
});

describe("describeOutcome", () => {
  test("says what happened, to the operator and — differently — to the agent", () => {
    const outcome = { restored: ["a.py", "b.py"], skipped: [{ path: "c.py", reason: "it has changed since the agent wrote it" }] };

    expect(describeOutcome("undo", outcome)).toBe("Undone: put back a.py, b.py. Skipped: c.py (it has changed since the agent wrote it).");
    expect(describeOutcome("undo", outcome, true)).toBe(
      "I undid your changes from the last turn to: a.py, b.py. Those files are as they were before that turn. Left as they are: c.py (it has changed since the agent wrote it).",
    );
    expect(describeOutcome("redo", { restored: ["a.py"], skipped: [] }, true)).toBe("I re-applied your changes that I had undone, to: a.py.");
    expect(describeOutcome("redo", { restored: [], skipped: [] })).toBe("Nothing to redo.");
  });
});
