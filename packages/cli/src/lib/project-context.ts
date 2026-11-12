import { existsSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative } from "node:path";

// What the chat agent is told about where it works: the environment its tools
// run in, and the project's own instructions file. Read fresh for every
// message, so an edit to the file or a branch switch takes effect at once.

/** Looked for in this order, in the working directory first and then in each parent up to the repository root. */
export const INSTRUCTION_FILES = ["AGENTS.md", "CERBERUS.md", "CLAUDE.md"];

export const MAX_INSTRUCTIONS = 20_000;

export type ProjectContext = {
  environment: {
    workingDirectory: string;
    platform: string;
    date: string;
    isGitRepository: boolean;
    gitBranch?: string;
  };
  instructions?: { file: string; content: string; truncated: boolean };
};

/** The repository root at or above `start` (the directory holding `.git`), or null outside a repository. */
export function findGitRoot(start: string): string | null {
  for (let dir = start; ; dir = dirname(dir)) {
    if (existsSync(join(dir, ".git"))) return dir;
    if (dirname(dir) === dir) return null;
  }
}

/** The checked-out branch, read from `.git/HEAD` without running git. Undefined when detached or unreadable. */
export function currentBranch(gitRoot: string): string | undefined {
  try {
    let gitDir = join(gitRoot, ".git");
    // A worktree or submodule has a `.git` file pointing at the real directory.
    if (statSync(gitDir).isFile()) {
      const pointer = readFileSync(gitDir, "utf-8").match(/^gitdir:\s*(.+)$/m)?.[1]?.trim();
      if (!pointer) return undefined;
      gitDir = pointer.startsWith("/") ? pointer : join(gitRoot, pointer);
    }
    return readFileSync(join(gitDir, "HEAD"), "utf-8").match(/^ref:\s*refs\/heads\/(.+)$/m)?.[1]?.trim();
  } catch {
    return undefined;
  }
}

/** The nearest instructions file: in `cwd`, else in a parent up to the repository root (or only `cwd` outside a repository). */
export function findInstructions(cwd: string, gitRoot: string | null): ProjectContext["instructions"] {
  const stop = gitRoot ?? cwd;
  for (let dir = cwd; ; dir = dirname(dir)) {
    for (const name of INSTRUCTION_FILES) {
      const path = join(dir, name);
      try {
        if (!statSync(path).isFile()) continue;
        const content = readFileSync(path, "utf-8").trim();
        if (content.length === 0) continue;
        return {
          file: relative(cwd, path) || name,
          content: content.slice(0, MAX_INSTRUCTIONS),
          truncated: content.length > MAX_INSTRUCTIONS,
        };
      } catch {
        // Not there, or not readable: try the next one.
      }
    }
    if (dir === stop || dirname(dir) === dir) return undefined;
  }
}

export function projectContext(cwd: string = process.cwd(), now: Date = new Date()): ProjectContext {
  const gitRoot = findGitRoot(cwd);
  const branch = gitRoot ? currentBranch(gitRoot) : undefined;
  const instructions = findInstructions(cwd, gitRoot);
  return {
    environment: {
      workingDirectory: cwd,
      platform: process.platform,
      date: `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`,
      isGitRepository: gitRoot !== null,
      ...(branch ? { gitBranch: branch } : {}),
    },
    ...(instructions ? { instructions } : {}),
  };
}
