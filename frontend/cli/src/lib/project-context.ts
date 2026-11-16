import { existsSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { cerberusHome } from "./home";
import { collectInstructions, nearestInstructions, readRegularPrefix, type InstructionFile, type LegacyInstructions } from "./instruction-files";

// What the chat agent is told about where it works: the environment its tools
// run in, and the global/project instruction hierarchy. Read fresh for every
// message, so an edit to the file or a branch switch takes effect at once.

export { INSTRUCTION_FILES, MAX_INSTRUCTIONS, MAX_INSTRUCTION_FILES } from "./instruction-files";

export type ProjectContext = {
  environment: {
    workingDirectory: string;
    platform: string;
    date: string;
    isGitRepository: boolean;
    gitBranch?: string;
  };
  /** Compatibility with backends that only know the nearest project file. */
  instructions?: LegacyInstructions;
  /** Ordered global → outer project → inner project. New backends use this instead of `instructions`. */
  instructionFiles?: InstructionFile[];
  /** More candidate sources existed than fit the 16-source limit. */
  instructionFilesTruncated?: boolean;
};

/** The repository root at or above `start` (the directory holding `.git`), or null outside a repository. */
export function findGitRoot(start: string): string | null {
  for (let dir = resolve(start); ; dir = dirname(dir)) {
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
      const pointer = readRegularPrefix(gitDir, 4_096)?.content.match(/^gitdir:\s*(.+)$/m)?.[1]?.trim();
      if (!pointer) return undefined;
      gitDir = pointer.startsWith("/") ? pointer : join(gitRoot, pointer);
    }
    return readRegularPrefix(join(gitDir, "HEAD"), 1_024)?.content.match(/^ref:\s*refs\/heads\/(.+)$/m)?.[1]?.trim();
  } catch {
    return undefined;
  }
}

/** The nearest instructions file: in `cwd`, else in a parent up to the repository root (or only `cwd` outside a repository). */
export function findInstructions(cwd: string, gitRoot: string | null): ProjectContext["instructions"] {
  return nearestInstructions(cwd, gitRoot);
}

export function projectContext(
  cwd: string = process.cwd(),
  now: Date = new Date(),
  options: { globalDirectory?: string | null } = {},
): ProjectContext {
  const gitRoot = findGitRoot(cwd);
  const branch = gitRoot ? currentBranch(gitRoot) : undefined;
  const instructions = findInstructions(cwd, gitRoot);
  const globalDirectory = options.globalDirectory === undefined ? cerberusHome() : options.globalDirectory;
  const hierarchy = collectInstructions(cwd, gitRoot, globalDirectory);
  return {
    environment: {
      workingDirectory: cwd,
      platform: process.platform,
      date: `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`,
      isGitRepository: gitRoot !== null,
      ...(branch ? { gitBranch: branch } : {}),
    },
    ...(instructions ? { instructions } : {}),
    // An explicit empty list also tells a new backend not to fall back to a
    // stale legacy field if the operator removed the applicable files.
    instructionFiles: hierarchy.files,
    ...(hierarchy.truncated ? { instructionFilesTruncated: true } : {}),
  };
}
