import { closeSync, constants, fstatSync, lstatSync, openSync, readSync, realpathSync } from "node:fs";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";

/** One file per directory; the first readable, non-empty name wins. */
export const INSTRUCTION_FILES = ["AGENTS.md", "CERBERUS.md", "CLAUDE.md"];
export const MAX_INSTRUCTIONS = 20_000;
export const MAX_INSTRUCTION_FILES = 16;
const RESERVED_PER_SOURCE = 1_024;

export type InstructionFile = {
  file: string;
  content: string;
  truncated: boolean;
  scope: "global" | "project";
  directory?: string;
};

export type LegacyInstructions = Pick<InstructionFile, "file" | "content" | "truncated">;
type Source = { directory: string; allowedRoot: string; scope: InstructionFile["scope"]; paths: string[] };

const inside = (root: string, path: string) => {
  const rel = relative(root, path);
  return rel === "" || (!isAbsolute(rel) && rel !== ".." && !rel.startsWith(`..${process.platform === "win32" ? "\\" : "/"}`));
};

/** Labels go into prompts: paths used to read files are kept separate from their bounded display names. */
function label(value: string, max: number): string {
  const clean = value.replace(/[\u0000-\u001f\u007f-\u009f\u2028\u2029]/g, " ");
  if (clean.length <= max) return clean;
  let end = max - 1;
  if (/^[\uDC00-\uDFFF]$/.test(clean[end] ?? "")) end--;
  return clean.slice(0, end) + "…";
}

/**
 * Reads at most `maxBytes + 4` bytes, never a whole large file. A partial UTF-8
 * character is left out. Symlinks and special files are refused before open;
 * O_NOFOLLOW/O_NONBLOCK and the descriptor check also cover a swapped final
 * path, so a FIFO cannot hang the client. An allowed root checks real paths.
 */
export function readRegularPrefix(path: string, maxBytes: number, allowedRoot?: string): { content: string; truncated: boolean } | undefined {
  let fd: number | undefined;
  try {
    const observed = lstatSync(path);
    if (!observed.isFile() || observed.isSymbolicLink()) return undefined;
    const actual = realpathSync(path);
    if (allowedRoot && !inside(realpathSync(allowedRoot), actual)) return undefined;
    fd = openSync(actual, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0) | (constants.O_NONBLOCK ?? 0));
    const opened = fstatSync(fd);
    if (!opened.isFile() || opened.dev !== observed.dev || opened.ino !== observed.ino) return undefined;
    const capacity = Math.min(Math.max(0, maxBytes) + 4, opened.size);
    const buffer = Buffer.alloc(capacity);
    const read = readSync(fd, buffer, 0, capacity, 0);
    const prefix = buffer.subarray(0, Math.min(maxBytes, read));
    const truncated = opened.size > prefix.length;
    const content = new TextDecoder("utf-8").decode(prefix, { stream: truncated }).trim();
    return { content, truncated };
  } catch {
    // Unreadable, gone or replaced: the next filename/directory may still work.
    return undefined;
  } finally {
    if (fd !== undefined) closeSync(fd);
  }
}

/** Only the launch directory and its ancestors inside this repository; never siblings or descendants. */
function projectDirectories(cwd: string, gitRoot: string | null): string[] {
  const start = resolve(cwd);
  const stop = gitRoot ? resolve(gitRoot) : start;
  if (!inside(stop, start)) return [];
  const directories: string[] = [];
  for (let dir = start; ; dir = dirname(dir)) {
    directories.push(dir);
    if (dir === stop || dirname(dir) === dir) break;
  }
  return directories.reverse();
}

function sourceAt(directory: string, allowedRoot: string, scope: Source["scope"]): Source | undefined {
  try {
    const root = realpathSync(allowedRoot);
    if (!inside(root, realpathSync(directory))) return undefined;
    const paths = INSTRUCTION_FILES.map((name) => join(directory, name)).filter((path) => {
      try {
        const stat = lstatSync(path);
        return stat.isFile() && !stat.isSymbolicLink() && stat.size > 0 && inside(root, realpathSync(path));
      } catch {
        return false;
      }
    });
    return paths.length ? { directory, allowedRoot, scope, paths } : undefined;
  } catch {
    return undefined;
  }
}

function readSource(source: Source, cwd: string, budget: number): InstructionFile | undefined {
  for (const path of source.paths) {
    const read = readRegularPrefix(path, budget, source.allowedRoot);
    if (!read?.content) continue;
    return {
      file: label(source.scope === "global" ? path : relative(cwd, path), 300),
      ...read,
      scope: source.scope,
      // Global instructions apply to every project, not just their storage directory.
      ...(source.scope === "project" ? { directory: label(relative(cwd, source.directory) || ".", 500) } : {}),
    };
  }
  return undefined;
}

/** The old contract: just the closest project file, independently limited to 20k for old backends. */
export function nearestInstructions(cwd: string, gitRoot: string | null): LegacyInstructions | undefined {
  const root = gitRoot ?? cwd;
  for (const directory of projectDirectories(cwd, gitRoot).reverse()) {
    const source = sourceAt(directory, root, "project");
    const read = source && readSource(source, resolve(cwd), MAX_INSTRUCTIONS);
    if (read) return { file: read.file, content: read.content, truncated: read.truncated };
  }
  return undefined;
}

/**
 * Global first, then repository root through the launch directory. With more
 * than 16 candidate directories, keep the global source, root and closest
 * ancestors. Within 20k, every later source reserves up to 1,024 characters;
 * earlier sources can use the rest, and unused space flows forward. This
 * keeps a giant global/root file from erasing all of the closest instructions.
 * Reads are byte-bounded, so multibyte text may use less than its character
 * allowance. Empty directories never count toward the source limit.
 */
export function collectInstructions(cwd: string, gitRoot: string | null, globalDirectory: string | null): { files: InstructionFile[]; truncated: boolean } {
  const workingDirectory = resolve(cwd);
  const projectRoot = gitRoot ? resolve(gitRoot) : workingDirectory;
  const projects = projectDirectories(workingDirectory, gitRoot)
    .map((directory) => sourceAt(directory, projectRoot, "project"))
    .filter((source): source is Source => source !== undefined);
  const globalRoot = globalDirectory === null ? null : resolve(globalDirectory);
  const global = globalRoot ? sourceAt(globalRoot, globalRoot, "global") : undefined;
  const capacity = MAX_INSTRUCTION_FILES - (global ? 1 : 0);
  // Retain the outermost applicable project source as well as the closest ones.
  const selectedProjects = projects.length > capacity
    ? [projects[0]!, ...projects.slice(-(capacity - 1))]
    : projects;
  const selected = [...(global ? [global] : []), ...selectedProjects];
  const files: InstructionFile[] = [];
  let remaining = MAX_INSTRUCTIONS;
  for (let i = 0; i < selected.length; i++) {
    const reserved = Math.min(RESERVED_PER_SOURCE, Math.floor(MAX_INSTRUCTIONS / selected.length));
    const budget = remaining - reserved * (selected.length - i - 1);
    const read = readSource(selected[i]!, workingDirectory, budget);
    if (read) {
      files.push(read);
      remaining -= read.content.length;
    }
  }
  return { files, truncated: projects.length > selectedProjects.length };
}
