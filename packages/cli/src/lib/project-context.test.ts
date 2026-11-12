import { afterAll, describe, expect, test } from "bun:test";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { currentBranch, findGitRoot, findInstructions, MAX_INSTRUCTIONS, projectContext } from "./project-context";

const root = mkdtempSync(join(tmpdir(), "nightcode-context-"));
afterAll(() => rmSync(root, { recursive: true, force: true }));

/** A directory tree: a repository on branch `feature/login` with a nested package, and a plain directory beside it. */
function layout() {
  const repo = join(root, `repo-${Math.random().toString(36).slice(2, 8)}`);
  mkdirSync(join(repo, ".git"), { recursive: true });
  writeFileSync(join(repo, ".git", "HEAD"), "ref: refs/heads/feature/login\n");
  mkdirSync(join(repo, "packages", "api", "src"), { recursive: true });
  const plain = join(root, `plain-${Math.random().toString(36).slice(2, 8)}`);
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
});
