import { afterAll, expect, test } from "bun:test";
import { execFileSync } from "node:child_process";
import { chmodSync, mkdirSync, mkdtempSync, rmSync, symlinkSync, truncateSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, relative } from "node:path";
import { collectInstructions, MAX_INSTRUCTION_FILES, MAX_INSTRUCTIONS, nearestInstructions, readRegularPrefix } from "./instruction-files";

const scratch = mkdtempSync(join(tmpdir(), "cerberus-instructions-"));
afterAll(() => rmSync(scratch, { recursive: true, force: true }));
let counter = 0;

function layout() {
  const root = join(scratch, String(++counter));
  const repo = join(root, "repo");
  const global = join(root, "global");
  const cwd = join(repo, "packages", "api");
  mkdirSync(cwd, { recursive: true });
  mkdirSync(global, { recursive: true });
  return { root, repo, global, cwd };
}

test("global and every applicable ancestor arrive in outer-to-inner order with one file per directory", () => {
  const { repo, global, cwd } = layout();
  writeFileSync(join(global, "AGENTS.md"), "Global.");
  writeFileSync(join(global, "CLAUDE.md"), "Wrong global fallback.");
  writeFileSync(join(repo, "AGENTS.md"), "Repository.");
  writeFileSync(join(repo, "packages", "CLAUDE.md"), "Packages.");
  writeFileSync(join(cwd, "CERBERUS.md"), "API.");
  writeFileSync(join(cwd, "CLAUDE.md"), "Wrong project fallback.");
  const result = collectInstructions(cwd, repo, global);
  expect(result.truncated).toBe(false);
  expect(result.files).toEqual([
    { file: join(global, "AGENTS.md"), content: "Global.", truncated: false, scope: "global" },
    { file: relative(cwd, join(repo, "AGENTS.md")), content: "Repository.", truncated: false, scope: "project", directory: "../.." },
    { file: "../CLAUDE.md", content: "Packages.", truncated: false, scope: "project", directory: ".." },
    { file: "CERBERUS.md", content: "API.", truncated: false, scope: "project", directory: "." },
  ]);
});

test("empty, whitespace-only and unreadable candidates fall through to the next name", () => {
  const { repo, global, cwd } = layout();
  writeFileSync(join(global, "AGENTS.md"), "");
  writeFileSync(join(global, "CERBERUS.md"), " \t\n");
  writeFileSync(join(global, "CLAUDE.md"), "Global fallback.");
  mkdirSync(join(cwd, "AGENTS.md"));
  writeFileSync(join(cwd, "CERBERUS.md"), "\r\n ");
  writeFileSync(join(cwd, "CLAUDE.md"), "Project fallback.");
  expect(collectInstructions(cwd, repo, global).files.map((source) => source.content)).toEqual(["Global fallback.", "Project fallback."]);
});

test("nothing above the repository, in siblings or below the launch directory is loaded", () => {
  const { root, repo, global, cwd } = layout();
  writeFileSync(join(root, "AGENTS.md"), "Above repository.");
  mkdirSync(join(repo, "other"));
  writeFileSync(join(repo, "other", "AGENTS.md"), "Sibling.");
  mkdirSync(join(cwd, "child"));
  writeFileSync(join(cwd, "child", "AGENTS.md"), "Descendant.");
  expect(collectInstructions(cwd, repo, global)).toEqual({ files: [], truncated: false });
  writeFileSync(join(repo, "AGENTS.md"), "Parent outside a repository.");
  expect(collectInstructions(cwd, null, global).files).toEqual([]);
});

test("outside a repository only the launch directory and explicit global root apply", () => {
  const { repo, global, cwd } = layout();
  writeFileSync(join(repo, "AGENTS.md"), "Do not read this parent.");
  writeFileSync(join(cwd, "AGENTS.md"), "Plain directory.");
  writeFileSync(join(global, "AGENTS.md"), "Global.");
  expect(collectInstructions(cwd, null, global).files.map((source) => source.content)).toEqual(["Global.", "Plain directory."]);
});

test("a giant global file cannot crowd out the root and closest instructions", () => {
  const { repo, global, cwd } = layout();
  writeFileSync(join(global, "AGENTS.md"), "g".repeat(MAX_INSTRUCTIONS * 2));
  writeFileSync(join(repo, "AGENTS.md"), "r".repeat(MAX_INSTRUCTIONS * 2));
  writeFileSync(join(cwd, "AGENTS.md"), "l".repeat(MAX_INSTRUCTIONS * 2));
  const result = collectInstructions(cwd, repo, global);
  expect(result.files).toHaveLength(3);
  expect(result.files.reduce((sum, source) => sum + source.content.length, 0)).toBe(MAX_INSTRUCTIONS);
  for (const source of result.files) {
    expect(source.content.length).toBeGreaterThanOrEqual(1_024);
    expect(source.truncated).toBe(true);
  }
  expect(nearestInstructions(cwd, repo)?.content).toBe("l".repeat(MAX_INSTRUCTIONS));
});

test("space unused by earlier files is available to the closest source", () => {
  const { repo, global, cwd } = layout();
  writeFileSync(join(global, "AGENTS.md"), "Global.");
  writeFileSync(join(repo, "AGENTS.md"), "Root.");
  writeFileSync(join(cwd, "AGENTS.md"), "x".repeat(MAX_INSTRUCTIONS * 2));
  const result = collectInstructions(cwd, repo, global);
  expect(result.files.at(-1)?.content.length).toBe(MAX_INSTRUCTIONS - "Global.".length - "Root.".length);
});

test("a sparse huge file is read as a bounded prefix", () => {
  const { repo } = layout();
  const file = join(repo, "AGENTS.md");
  writeFileSync(file, "x".repeat(MAX_INSTRUCTIONS + 10));
  truncateSync(file, 256 * 1024 * 1024);
  expect(readRegularPrefix(file, MAX_INSTRUCTIONS, repo)).toEqual({ content: "x".repeat(MAX_INSTRUCTIONS), truncated: true });
});

test("UTF-8 truncation keeps complete characters and no replacement character", () => {
  const { repo } = layout();
  const file = join(repo, "AGENTS.md");
  writeFileSync(file, "a".repeat(MAX_INSTRUCTIONS - 1) + "🚀 tail");
  const read = readRegularPrefix(file, MAX_INSTRUCTIONS, repo)!;
  expect(read.content).toBe("a".repeat(MAX_INSTRUCTIONS - 1));
  expect(read.truncated).toBe(true);
  writeFileSync(file, "Код и тесты 🚀");
  expect(readRegularPrefix(file, MAX_INSTRUCTIONS, repo)).toEqual({ content: "Код и тесты 🚀", truncated: false });
});

test("the source limit retains global, root and closest ancestors in their original order", () => {
  const { repo, global } = layout();
  writeFileSync(join(global, "AGENTS.md"), "Global.");
  writeFileSync(join(repo, "AGENTS.md"), "Root.");
  let cwd = repo;
  for (let depth = 1; depth <= 20; depth++) {
    cwd = join(cwd, `d${depth}`);
    mkdirSync(cwd);
    writeFileSync(join(cwd, "AGENTS.md"), `Depth ${depth}.`);
  }
  const result = collectInstructions(cwd, repo, global);
  expect(result.truncated).toBe(true);
  expect(result.files).toHaveLength(MAX_INSTRUCTION_FILES);
  expect(result.files.map((source) => source.content)).toEqual([
    "Global.", "Root.", ...Array.from({ length: 14 }, (_, index) => `Depth ${index + 7}.`),
  ]);
});

test("empty directories do not use up source slots or mark the hierarchy incomplete", () => {
  const { repo, global } = layout();
  writeFileSync(join(repo, "AGENTS.md"), "Root.");
  let cwd = repo;
  for (let depth = 0; depth < 25; depth++) {
    cwd = join(cwd, "d");
    mkdirSync(cwd);
    writeFileSync(join(cwd, "AGENTS.md"), "");
  }
  writeFileSync(join(cwd, "AGENTS.md"), "Closest.");
  expect(collectInstructions(cwd, repo, global).files.map((source) => source.content)).toEqual(["Root.", "Closest."]);
  expect(collectInstructions(cwd, repo, global).truncated).toBe(false);
});

test("symlink instruction files are skipped even if they point inside the allowed root", () => {
  const { repo, global, cwd } = layout();
  writeFileSync(join(global, "outside.md"), "Outside instructions.");
  symlinkSync(join(global, "outside.md"), join(cwd, "AGENTS.md"));
  writeFileSync(join(cwd, "inside.md"), "Inside alias.");
  symlinkSync(join(cwd, "inside.md"), join(cwd, "CERBERUS.md"));
  writeFileSync(join(cwd, "CLAUDE.md"), "Actual file.");
  expect(collectInstructions(cwd, repo, null).files.map((source) => source.content)).toEqual(["Actual file."]);
  expect(readRegularPrefix(join(cwd, "AGENTS.md"), MAX_INSTRUCTIONS, repo)).toBeUndefined();
});

test("a directory symlink cannot import instructions from outside the project", () => {
  const { repo, global } = layout();
  writeFileSync(join(repo, "AGENTS.md"), "Root.");
  writeFileSync(join(global, "AGENTS.md"), "Escaped.");
  const linked = join(repo, "linked");
  symlinkSync(global, linked, "dir");
  expect(collectInstructions(linked, repo, null).files.map((source) => source.content)).toEqual(["Root."]);
});

test("a configured global root may itself be an explicit directory symlink", () => {
  const { root, repo, global } = layout();
  writeFileSync(join(global, "AGENTS.md"), "Explicit global root.");
  const linked = join(root, "global-link");
  symlinkSync(global, linked, "dir");
  expect(collectInstructions(repo, repo, linked).files.map((source) => source.content)).toEqual(["Explicit global root."]);
});

test("missing files and directories are harmless, and a foreign root cannot widen the search", () => {
  const { repo, global, cwd } = layout();
  writeFileSync(join(cwd, "AGENTS.md"), "Private.");
  expect(readRegularPrefix(join(repo, "missing"), MAX_INSTRUCTIONS, repo)).toBeUndefined();
  expect(readRegularPrefix(repo, MAX_INSTRUCTIONS, repo)).toBeUndefined();
  expect(collectInstructions(cwd, global, null).files).toEqual([]);
  expect(collectInstructions(cwd, repo, join(repo, "missing")).files.map((source) => source.content)).toEqual(["Private."]);
});

test("prompt labels are bounded and cannot add lines or terminal control characters", () => {
  const { root, repo } = layout();
  let global = join(root, "global\n\u001b-name");
  for (let depth = 0; depth < 4; depth++) global = join(global, "long".repeat(40));
  mkdirSync(global, { recursive: true });
  writeFileSync(join(global, "AGENTS.md"), "Global.");
  const source = collectInstructions(repo, repo, global).files[0]!;
  expect(source.file.length).toBeLessThanOrEqual(300);
  expect(source.directory).toBeUndefined();
  expect(source.file).not.toMatch(/[\r\n\u001b]/);
  expect(source.content).toBe("Global.");
});

test("unreadable files do not prevent a lower-priority filename from loading", () => {
  if (process.getuid?.() === 0 || process.platform === "win32") return;
  const { repo } = layout();
  const protectedFile = join(repo, "AGENTS.md");
  writeFileSync(protectedFile, "Unreadable.");
  writeFileSync(join(repo, "CERBERUS.md"), "Fallback.");
  chmodSync(protectedFile, 0);
  try {
    expect(collectInstructions(repo, repo, null).files.map((source) => source.content)).toEqual(["Fallback."]);
  } finally {
    chmodSync(protectedFile, 0o600);
  }
});

test("a FIFO named as instructions is rejected without waiting for a writer", () => {
  if (process.platform === "win32") return;
  const { repo } = layout();
  const fifo = join(repo, "AGENTS.md");
  execFileSync("mkfifo", [fifo]);
  writeFileSync(join(repo, "CLAUDE.md"), "Regular fallback.");
  expect(readRegularPrefix(fifo, MAX_INSTRUCTIONS, repo)).toBeUndefined();
  expect(collectInstructions(repo, repo, null).files.map((source) => source.content)).toEqual(["Regular fallback."]);
});
