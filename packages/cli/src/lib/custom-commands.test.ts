import { afterAll, describe, expect, test } from "bun:test";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { expandSubmission, expandTemplate, loadCustomCommands, parseCommandFile, splitArguments, type CustomCommand } from "./custom-commands";

const root = mkdtempSync(join(tmpdir(), "cerberus-commands-"));
afterAll(() => rmSync(root, { recursive: true, force: true }));

function project() {
  const repo = join(root, `repo-${Math.random().toString(36).slice(2, 8)}`);
  mkdirSync(join(repo, ".git"), { recursive: true });
  mkdirSync(join(repo, "packages", "api"), { recursive: true });
  const user = join(root, `user-${Math.random().toString(36).slice(2, 8)}`);
  mkdirSync(user, { recursive: true });
  const write = (dir: string, file: string, text: string) => {
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, file), text);
  };
  return { repo, nested: join(repo, "packages", "api"), user, write };
}

describe("parseCommandFile", () => {
  test("frontmatter gives the description; the rest is the prompt", () => {
    expect(parseCommandFile('---\ndescription: "Fix the failing tests"\nagent: build\n---\n\nRun the tests and fix $ARGUMENTS.\n')).toEqual({
      description: "Fix the failing tests",
      template: "Run the tests and fix $ARGUMENTS.",
    });
  });

  test("without frontmatter the first line describes it", () => {
    expect(parseCommandFile("# Review the diff\nLook at `git diff` and comment.\r\n")).toEqual({
      description: "Review the diff",
      template: "# Review the diff\nLook at `git diff` and comment.",
    });
  });
});

describe("loadCustomCommands", () => {
  test("finds the project's commands and the user's; nearer ones hide farther ones of the same name", () => {
    const { repo, nested, user, write } = project();
    write(join(repo, ".cerberus", "commands"), "fix-tests.md", "From the repo root.");
    write(join(repo, ".claude", "commands"), "explain.md", "Explain $1.");
    write(join(nested, ".cerberus", "commands"), "fix-tests.md", "From the package.");
    write(user, "fix-tests.md", "From the user.");
    write(user, "standup.md", "---\ndescription: Summarise my day\n---\nWhat did I change today?");

    const commands = loadCustomCommands(nested, user);

    expect(commands.map((c) => `${c.name}:${c.source}`)).toEqual(["fix-tests:project", "explain:project", "standup:user"]);
    expect(commands[0]!.template).toBe("From the package.");
    expect(commands[2]!.description).toBe("Summarise my day");
  });

  test("a built-in name cannot be taken, odd file names are skipped, and nothing above the repository is read", () => {
    const { repo, user, write } = project();
    write(join(repo, ".cerberus", "commands"), "scan.md", "Not the real scan.");
    write(join(repo, ".cerberus", "commands"), "Has Spaces.md", "x");
    write(join(repo, ".cerberus", "commands"), "notes.txt", "x");
    write(join(repo, ".cerberus", "commands"), "empty.md", "   \n");
    write(join(repo, ".cerberus", "commands"), "ok.md", "Fine.");
    write(join(root, ".cerberus", "commands"), "outside.md", "From above the repository.");
    try {
      expect(loadCustomCommands(repo, user, new Set(["scan"])).map((c) => c.name)).toEqual(["ok"]);
    } finally {
      rmSync(join(root, ".cerberus"), { recursive: true, force: true });
    }
  });

  test("with no command directories there are no custom commands", () => {
    const { repo, user } = project();
    expect(loadCustomCommands(repo, join(user, "missing"))).toEqual([]);
  });
});

describe("expandTemplate", () => {
  test("$ARGUMENTS is everything typed; $1, $2 are single arguments and the last one takes the rest", () => {
    expect(expandTemplate("Fix $ARGUMENTS now.", "  the login test  ")).toBe("Fix the login test now.");
    expect(expandTemplate("Rename $1 to $2.", "foo bar")).toBe("Rename foo to bar.");
    expect(expandTemplate("In $1: $2", "auth.py explain the token check")).toBe("In auth.py: explain the token check");
    expect(expandTemplate('Open $1 then $2', '"my file.txt" second')).toBe("Open my file.txt then second");
    expect(expandTemplate("Rename $1 to $2.", "only")).toBe("Rename only to .");
  });

  test("a template without placeholders gets what was typed appended; with nothing typed it is sent as it is", () => {
    expect(expandTemplate("Review the diff.", "focus on auth")).toBe("Review the diff.\n\nfocus on auth");
    expect(expandTemplate("Review the diff.", "")).toBe("Review the diff.");
  });

  test("what an argument contains is text, never another placeholder", () => {
    expect(expandTemplate("Say $1 and $2.", "$2 literal")).toBe("Say $2 and literal.");
    expect(expandTemplate("Do $ARGUMENTS", "$1 $ARGUMENTS")).toBe("Do $1 $ARGUMENTS");
  });

  test("arguments split on spaces, with quotes keeping words together", () => {
    expect(splitArguments(`a "b c" 'd e' f`)).toEqual(["a", "b c", "d e", "f"]);
    expect(splitArguments("   ")).toEqual([]);
  });
});

describe("expandSubmission", () => {
  const commands: CustomCommand[] = [{ name: "fix-tests", description: "", template: "Run the tests and fix $ARGUMENTS.", source: "project" }];

  test("a line that names a custom command is sent as its filled prompt", () => {
    expect(expandSubmission("/fix-tests the auth ones", commands)).toBe("Run the tests and fix the auth ones.");
    expect(expandSubmission("/FIX-TESTS", commands)).toBe("Run the tests and fix .");
    expect(expandSubmission("/fix-tests\nline two", commands)).toBe("Run the tests and fix line two.");
  });

  test("any other line is sent as it is", () => {
    expect(expandSubmission("/unknown thing", commands)).toBeNull();
    expect(expandSubmission("please /fix-tests", commands)).toBeNull();
    expect(expandSubmission("/fix-testsx", commands)).toBeNull();
    expect(expandSubmission("just text", commands)).toBeNull();
  });
});
