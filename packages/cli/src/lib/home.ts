import { existsSync, mkdirSync, readdirSync, renameSync, rmdirSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

// Where the CLI keeps its own files: the sign-in, the chosen engagement and
// theme, the undo history, the operator's own slash commands.
// CERBERUS_HOME moves it (tests, scripts, a pipeline's scratch directory).

/** What the directory was called before the tool was renamed. Kept only so that an existing sign-in is carried over. */
const FORMER_NAME = ".nightcode";

let migrated = false;

/**
 * Moves what the tool kept under its former name into its directory of today:
 * every entry that is not there yet. Nothing that already exists is
 * overwritten, and the old directory is removed only once it is empty.
 * Returns the names moved.
 */
export function carryOver(former: string, current: string): string[] {
  if (!existsSync(former)) return [];
  const moved: string[] = [];
  mkdirSync(current, { recursive: true, mode: 0o700 });
  for (const name of readdirSync(former)) {
    if (existsSync(join(current, name))) continue;
    renameSync(join(former, name), join(current, name));
    moved.push(name);
  }
  if (readdirSync(former).length === 0) rmdirSync(former);
  return moved;
}

export function cerberusHome(): string {
  if (process.env.CERBERUS_HOME) return process.env.CERBERUS_HOME;
  const current = join(homedir(), ".cerberus");
  // Once per start, and never as a side effect of the test suite.
  if (!migrated && process.env.NODE_ENV !== "test") {
    migrated = true;
    try {
      carryOver(join(homedir(), FORMER_NAME), current);
    } catch {
      // Not being able to carry the old files over only means signing in again.
    }
  }
  return current;
}
