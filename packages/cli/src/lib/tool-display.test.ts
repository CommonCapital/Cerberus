import { describe, expect, test } from "bun:test";
import { summarizeToolCall, toolResultLines } from "./tool-display";

describe("summarizeToolCall", () => {
  test("says what the call is about, in a few words", () => {
    expect(summarizeToolCall("readFile", { path: "src/app.ts" })).toBe("src/app.ts");
    expect(summarizeToolCall("readFile", { path: "src/app.ts", offset: 2001 })).toBe("src/app.ts from line 2001");
    expect(summarizeToolCall("grep", { pattern: "os\\.system", path: "svc" })).toBe("os\\.system in svc");
    expect(summarizeToolCall("glob", { pattern: "**/*.ts", path: "." })).toBe("**/*.ts");
    expect(summarizeToolCall("bash", { command: "npm test\n  --watch=false" })).toBe("npm test --watch=false");
    expect(summarizeToolCall("loadSkill", { name: "tls-review" })).toBe("tls-review");
  });

  test("never prints a file's content or the text of an edit", () => {
    expect(summarizeToolCall("writeFile", { path: "notes.md", content: "SECRET ".repeat(500) })).toBe("notes.md");
    expect(summarizeToolCall("editFile", { path: "a.py", oldString: "x".repeat(900), newString: "y" })).toBe("a.py");
  });

  test("a long command and an unknown tool are cut short", () => {
    expect(summarizeToolCall("bash", { command: "x".repeat(300) })).toHaveLength(101);
    expect(summarizeToolCall("mystery", { a: "one", b: 2 })).toBe("one 2");
  });
});

describe("toolResultLines", () => {
  test("an edit shows how much changed and the change itself, coloured by line", () => {
    const lines = toolResultLines("editFile", { success: true, replacements: 1, added: 2, removed: 1, diff: "@@ line 4 @@\n-old\n+new\n+newer" });

    expect(lines).toEqual([
      { text: "1 replacement(s) · +2 −1", tone: "plain" },
      { text: "@@ line 4 @@", tone: "plain" },
      { text: "-old", tone: "removed" },
      { text: "+new", tone: "added" },
      { text: "+newer", tone: "added" },
    ]);
  });

  test("an inexact match is pointed out, and a long change is cut", () => {
    const diff = ["@@ line 1 @@", ...Array.from({ length: 30 }, (_, i) => `+line ${i}`)].join("\n");

    const lines = toolResultLines("editFile", { replacements: 1, added: 30, removed: 0, diff, note: "oldString did not match exactly; it matched ignoring typography." });

    expect(lines[1]).toEqual({ text: "oldString did not match exactly; it matched ignoring typography.", tone: "problem" });
    expect(lines.at(-1)).toEqual({ text: "… 17 more line(s) of the change", tone: "plain" });
    expect(lines).toHaveLength(2 + 14 + 1);
  });

  test("a written file says whether it is new", () => {
    expect(toolResultLines("writeFile", { created: true, added: 12, removed: 0 })[0]!.text).toBe("created · +12 −0");
    expect(toolResultLines("writeFile", { created: false, added: 3, removed: 9, diff: "@@ line 1 @@\n-a" })[0]!.text).toBe("overwritten · +3 −9");
  });

  test("a command shows a failure and the end of what it printed", () => {
    expect(toolResultLines("bash", { exitCode: 0, stdout: "building\nok\n", stderr: "" })).toEqual([
      { text: "building", tone: "plain" },
      { text: "ok", tone: "plain" },
    ]);
    expect(toolResultLines("bash", { exitCode: 2, stdout: "1\n2\n3\n4\n", stderr: "boom\n" })).toEqual([
      { text: "exit 2", tone: "problem" },
      { text: "3", tone: "plain" },
      { text: "4", tone: "plain" },
      { text: "boom", tone: "plain" },
    ]);
    expect(toolResultLines("bash", { exitCode: 143, stdout: "", stderr: "", timedOut: true, note: "Stopped after 120 s." })[0]).toEqual({ text: "Stopped after 120 s.", tone: "problem" });
  });

  test("reads, searches and listings say how much came back, only when that adds something", () => {
    expect(toolResultLines("readFile", { content: "1\tx", startLine: 1, endLine: 1, totalLines: 1, truncated: false })).toEqual([]);
    expect(toolResultLines("readFile", { startLine: 1, endLine: 2000, totalLines: 5210, truncated: true })).toEqual([{ text: "lines 1–2000 of 5210", tone: "plain" }]);
    expect(toolResultLines("grep", { matches: [] })).toEqual([{ text: "no matches", tone: "plain" }]);
    expect(toolResultLines("grep", { matches: new Array(50), truncated: true, totalMatches: 312 })).toEqual([{ text: "50 matches of 312", tone: "plain" }]);
    expect(toolResultLines("glob", { files: ["a"], truncated: true })).toEqual([{ text: "1 file (more exist)", tone: "plain" }]);
    expect(toolResultLines("mystery", { anything: 1 })).toEqual([]);
    expect(toolResultLines("bash", null)).toEqual([]);
  });
});
