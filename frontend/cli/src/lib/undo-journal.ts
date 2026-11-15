import { existsSync, mkdirSync, readFileSync, rmSync, unlinkSync, writeFileSync } from "node:fs";
import { cerberusHome } from "./home";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";

// Undo for what the agent did to files. Every write and edit the CLI carries
// out for the agent is recorded with the file's content before and after, per
// conversation and per turn (the work between two of the operator's prompts).
// "Undo" puts back what a turn replaced; "redo" re-applies it. A file the
// operator has changed since is never overwritten: it is skipped and named.
// Only the write and edit tools are covered — what a shell command changed is not.

/** Content beyond this is not kept; such a change cannot be undone and is reported as such. */
export const MAX_KEPT_BYTES = 1_000_000;
const MAX_ENTRIES = 300;

export type Change = { path: string; before: string | null; after: string };

type Entry = {
  seq: number;
  turn: string;
  /** The project directory the change was made in; paths are relative to it. */
  cwd: string;
  path: string;
  /** False when the file did not exist before. */
  existed: boolean;
  /** False when the content was too large to keep. */
  kept: boolean;
};

type Index = { next: number; entries: Entry[]; undone: string[] };

export type Outcome = {
  /** Files put back (undo) or changed again (redo). */
  restored: string[];
  /** Files left alone, each with the reason. */
  skipped: { path: string; reason: string }[];
};

export class UndoJournal {
  constructor(private readonly directory: string) {}

  private indexFile = () => join(this.directory, "index.json");
  private blob = (seq: number, side: "before" | "after") => join(this.directory, `${seq}.${side}`);

  private read(): Index {
    try {
      const parsed = JSON.parse(readFileSync(this.indexFile(), "utf-8")) as Partial<Index>;
      return { next: parsed.next ?? 1, entries: parsed.entries ?? [], undone: parsed.undone ?? [] };
    } catch {
      return { next: 1, entries: [], undone: [] };
    }
  }

  private write(index: Index) {
    mkdirSync(this.directory, { recursive: true, mode: 0o700 });
    writeFileSync(this.indexFile(), JSON.stringify(index), { mode: 0o600 });
  }

  private drop(entry: Entry) {
    for (const side of ["before", "after"] as const) {
      try {
        unlinkSync(this.blob(entry.seq, side));
      } catch {
        // Never written, or gone already.
      }
    }
  }

  /** Records one change the agent made, as part of `turn`. A new change ends the chance to redo what was undone. */
  record(turn: string, cwd: string, change: Change) {
    const index = this.read();
    // What was undone and is now being built over can no longer be re-applied.
    if (index.undone.length > 0) {
      for (const entry of index.entries.filter((e) => index.undone.includes(e.turn))) this.drop(entry);
      index.entries = index.entries.filter((e) => !index.undone.includes(e.turn));
      index.undone = [];
    }

    const kept =
      Buffer.byteLength(change.after, "utf-8") <= MAX_KEPT_BYTES &&
      (change.before === null || Buffer.byteLength(change.before, "utf-8") <= MAX_KEPT_BYTES);
    const entry: Entry = { seq: index.next++, turn, cwd, path: change.path, existed: change.before !== null, kept };
    mkdirSync(this.directory, { recursive: true, mode: 0o700 });
    if (kept) {
      if (change.before !== null) writeFileSync(this.blob(entry.seq, "before"), change.before, { mode: 0o600 });
      writeFileSync(this.blob(entry.seq, "after"), change.after, { mode: 0o600 });
    }
    index.entries.push(entry);
    while (index.entries.length > MAX_ENTRIES) this.drop(index.entries.shift()!);
    this.write(index);
  }

  /** The turns that have changes, oldest first, without those already undone. */
  private liveTurns(index: Index): string[] {
    return [...new Set(index.entries.map((e) => e.turn))].filter((turn) => !index.undone.includes(turn));
  }

  canUndo = () => this.liveTurns(this.read()).length > 0;
  canRedo = () => this.read().undone.length > 0;

  /** Puts back what the latest turn replaced. Null when there is nothing to undo. */
  undo(cwd: string): Outcome | null {
    const index = this.read();
    const turn = this.liveTurns(index).at(-1);
    if (turn === undefined) return null;

    const outcome: Outcome = { restored: [], skipped: [] };
    // Latest change first, so a file edited twice in the turn goes back step by step.
    for (const entry of index.entries.filter((e) => e.turn === turn).reverse()) {
      const full = this.locate(entry, cwd, outcome);
      if (full === null) continue;
      const current = readOrNull(full);
      if (current !== readOrNull(this.blob(entry.seq, "after"))) {
        skip(outcome, entry.path, "it has changed since the agent wrote it");
        continue;
      }
      if (entry.existed) writeFileSync(full, readFileSync(this.blob(entry.seq, "before"), "utf-8"));
      else rmSync(full);
      if (!outcome.restored.includes(entry.path)) outcome.restored.push(entry.path);
    }
    index.undone.push(turn);
    this.write(index);
    return outcome;
  }

  /** Re-applies the turn that was undone last. Null when there is nothing to redo. */
  redo(cwd: string): Outcome | null {
    const index = this.read();
    const turn = index.undone.at(-1);
    if (turn === undefined) return null;

    const outcome: Outcome = { restored: [], skipped: [] };
    for (const entry of index.entries.filter((e) => e.turn === turn)) {
      const full = this.locate(entry, cwd, outcome);
      if (full === null) continue;
      const current = readOrNull(full);
      const expected = entry.existed ? readOrNull(this.blob(entry.seq, "before")) : null;
      if (current !== expected) {
        skip(outcome, entry.path, "it has changed since the undo");
        continue;
      }
      mkdirSync(dirname(full), { recursive: true });
      writeFileSync(full, readFileSync(this.blob(entry.seq, "after"), "utf-8"));
      if (!outcome.restored.includes(entry.path)) outcome.restored.push(entry.path);
    }
    index.undone.pop();
    this.write(index);
    return outcome;
  }

  /** The file an entry is about, or null (with the reason noted) when it cannot be touched. */
  private locate(entry: Entry, cwd: string, outcome: Outcome): string | null {
    if (entry.cwd !== cwd) {
      skip(outcome, entry.path, `it was changed in another directory (${entry.cwd})`);
      return null;
    }
    if (!entry.kept) {
      skip(outcome, entry.path, "it was too large to keep a copy of");
      return null;
    }
    const full = resolve(cwd, entry.path);
    const inside = relative(cwd, full);
    if (inside.startsWith("..") || isAbsolute(inside)) {
      skip(outcome, entry.path, "it is outside the project directory");
      return null;
    }
    return full;
  }
}

const readOrNull = (path: string) => (existsSync(path) ? readFileSync(path, "utf-8") : null);

function skip(outcome: Outcome, path: string, reason: string) {
  if (!outcome.skipped.some((s) => s.path === path)) outcome.skipped.push({ path, reason });
  // A file that could not be put back in one step is not reported as restored by an earlier one.
  outcome.restored = outcome.restored.filter((p) => p !== path);
}

/** The journal of one conversation, kept beside the CLI's other files. */
export function journalFor(sessionId: string): UndoJournal {
  const safe = sessionId.replace(/[^A-Za-z0-9_-]/g, "_");
  return new UndoJournal(join(cerberusHome(), "undo", safe));
}

/** What an undo or redo did, in a sentence for the operator — and, with `forAgent`, as the operator would tell the agent. */
export function describeOutcome(kind: "undo" | "redo", outcome: Outcome, forAgent = false): string {
  const did = kind === "undo" ? "put back" : "re-applied";
  const parts: string[] = [];
  if (outcome.restored.length > 0) {
    parts.push(
      forAgent
        ? kind === "undo"
          ? `I undid your changes from the last turn to: ${outcome.restored.join(", ")}. Those files are as they were before that turn.`
          : `I re-applied your changes that I had undone, to: ${outcome.restored.join(", ")}.`
        : `${kind === "undo" ? "Undone" : "Redone"}: ${did} ${outcome.restored.join(", ")}.`,
    );
  }
  if (outcome.skipped.length > 0) {
    parts.push(`${forAgent ? "Left as they are" : "Skipped"}: ${outcome.skipped.map((s) => `${s.path} (${s.reason})`).join("; ")}.`);
  }
  return parts.length > 0 ? parts.join(" ") : `Nothing to ${kind}.`;
}
