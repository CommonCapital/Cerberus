import { afterAll, describe, expect, test } from "bun:test";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { currentBranch, findGitRoot, findInstructions, MAX_INSTRUCTIONS, projectContext as contextWithOptions } from "./project-context";

const root = mkdtempSync(join(tmpdir(), "cerberus-context-"));
afterAll(() => rmSync(root, { recursive: true, force: true }));
let nextLayout = 0;
// Never read the operator's real global instructions from a test.
const projectContext = (cwd: string, now?: Date) => contextWithOptions(cwd, now, { globalDirectory: null });

/** A directory tree: a repository on branch `feature/login` with a nested package, and a plain directory beside it. */
function layout() {
  const id = ++nextLayout;
  const repo = join(root, `repo-${id}`);
  mkdirSync(join(repo, ".git"), { recursive: true });
  writeFileSync(join(repo, ".git", "HEAD"), "ref: refs/heads/feature/login\n");
  mkdirSync(join(repo, "packages", "api", "src"), { recursive: true });
  const plain = join(root, `plain-${id}`);
  mkdirSync(plain, { recursive: true });
  return { repo, nested: join(repo, "packages", "api", "src"), plain };
}

describe("the environment", () => {
  test("says where the tools run, the date, and the branch of the repository", () => {
    const { repo, nested } = layout();

    const context = projectContext(nested, new Date(2026, 10, 3));

    expect(context.environment).toEqual({
      workingDirectory: nested,
      platform: process.platform,
      date: "2026-11-03",
      isGitRepository: true,
      gitBranch: "feature/login",
    });
    expect(findGitRoot(nested)).toBe(repo);
    expect(context.instructions).toBeUndefined();
  });

  test("outside a repository there is no branch; a detached head has none either", () => {
    const { repo, plain } = layout();
    expect(projectContext(plain).environment.isGitRepository).toBe(false);
    expect(projectContext(plain).environment.gitBranch).toBeUndefined();

    writeFileSync(join(repo, ".git", "HEAD"), "4b0caf4d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b\n");
    expect(currentBranch(repo)).toBeUndefined();
  });

  test("a worktree's branch is read from its relative gitdir pointer", () => {
    const { repo, plain } = layout();
    writeFileSync(join(plain, ".git"), `gitdir: ../${repo.split("/").at(-1)}/.git\n`);
    expect(currentBranch(plain)).toBe("feature/login");
  });
});

describe("project instructions", () => {
  test("the nearest file wins: the working directory first, then parents up to the repository root", () => {
    const { repo, nested } = layout();
    writeFileSync(join(repo, "AGENTS.md"), "Run the tests with make test.\n");
    expect(projectContext(nested).instructions).toEqual({ file: "../../../AGENTS.md", content: "Run the tests with make test.", truncated: false });

    writeFileSync(join(repo, "packages", "api", "CLAUDE.md"), "API notes.");
    expect(projectContext(nested).instructions?.file).toBe("../CLAUDE.md");

    writeFileSync(join(nested, "AGENTS.md"), "Closest.");
    expect(projectContext(nested).instructions).toEqual({ file: "AGENTS.md", content: "Closest.", truncated: false });
  });

  test("AGENTS.md is preferred over the other names in the same directory; an empty file is skipped", () => {
    const { repo } = layout();
    writeFileSync(join(repo, "CLAUDE.md"), "From CLAUDE.md");
    writeFileSync(join(repo, "CERBERUS.md"), "From CERBERUS.md");
    expect(projectContext(repo).instructions?.content).toBe("From CERBERUS.md");

    writeFileSync(join(repo, "AGENTS.md"), "   \n");
    expect(projectContext(repo).instructions?.content).toBe("From CERBERUS.md");
    writeFileSync(join(repo, "AGENTS.md"), "From AGENTS.md");
    expect(projectContext(repo).instructions?.content).toBe("From AGENTS.md");
  });

  test("nothing above the repository root, or above the working directory outside a repository, is read", () => {
    const { repo, plain } = layout();
    writeFileSync(join(root, "AGENTS.md"), "Outside every project.");
    try {
      expect(findInstructions(repo, repo)).toBeUndefined();
      expect(findInstructions(plain, null)).toBeUndefined();
    } finally {
      rmSync(join(root, "AGENTS.md"));
    }
  });

  test("a very long file is cut and marked as cut", () => {
    const { repo } = layout();
    writeFileSync(join(repo, "AGENTS.md"), "x".repeat(MAX_INSTRUCTIONS + 50));

    const instructions = projectContext(repo).instructions!;

    expect(instructions.content.length).toBe(MAX_INSTRUCTIONS);
    expect(instructions.truncated).toBe(true);
  });

  test("the new hierarchy is authoritative and legacy nearest instructions remain available", () => {
    const { repo, nested, plain: global } = layout();
    writeFileSync(join(global, "AGENTS.md"), "Global preferences.");
    writeFileSync(join(repo, "AGENTS.md"), "Repository rules.");
    writeFileSync(join(nested, "CERBERUS.md"), "Local rules.");
    const context = contextWithOptions(nested, undefined, { globalDirectory: global });
    expect(context.instructionFiles?.map((source) => [source.scope, source.content])).toEqual([
      ["global", "Global preferences."], ["project", "Repository rules."], ["project", "Local rules."],
    ]);
    expect(context.instructions).toEqual({ file: "CERBERUS.md", content: "Local rules.", truncated: false });
    expect(context.instructionFilesTruncated).toBeUndefined();
  });

  test("CERBERUS_HOME selects global instructions without touching the real home", () => {
    const { repo, plain: global } = layout();
    writeFileSync(join(global, "CLAUDE.md"), "Home override.");
    const before = process.env.CERBERUS_HOME;
    process.env.CERBERUS_HOME = global;
    try {
      expect(contextWithOptions(repo).instructionFiles?.map((source) => source.content)).toEqual(["Home override."]);
      expect(contextWithOptions(repo, undefined, { globalDirectory: null }).instructionFiles).toEqual([]);
    } finally {
      if (before === undefined) delete process.env.CERBERUS_HOME;
      else process.env.CERBERUS_HOME = before;
    }
  });

  test("instruction edits and removals take effect on the next context", () => {
    const { repo, plain: global } = layout();
    writeFileSync(join(global, "AGENTS.md"), "Before.");
    expect(contextWithOptions(repo, undefined, { globalDirectory: global }).instructionFiles?.[0]?.content).toBe("Before.");
    writeFileSync(join(global, "AGENTS.md"), "After.");
    expect(contextWithOptions(repo, undefined, { globalDirectory: global }).instructionFiles?.[0]?.content).toBe("After.");
    rmSync(join(global, "AGENTS.md"));
    expect(contextWithOptions(repo, undefined, { globalDirectory: global }).instructionFiles).toEqual([]);
  });
});
