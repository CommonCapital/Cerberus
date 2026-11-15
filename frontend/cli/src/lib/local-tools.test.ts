import { describe, expect, test } from "bun:test";
import { changeSummary, fitOutput, parseGrepLine, readNumbered, replaceExact } from "./local-tools";

describe("readNumbered", () => {
  test("a short file comes back whole, numbered, without a phantom last line", () => {
    expect(readNumbered("import os\nprint(1)\n")).toEqual({
      content: "1\timport os\n2\tprint(1)",
      startLine: 1,
      endLine: 2,
      totalLines: 2,
      truncated: false,
      next: null,
    });
  });

  test("a window is read with offset and limit, and says where to continue", () => {
    const file = Array.from({ length: 10 }, (_, i) => `line ${i + 1}`).join("\n");

    const window = readNumbered(file, 4, 3);

    expect(window.content).toBe("4\tline 4\n5\tline 5\n6\tline 6");
    expect(window).toMatchObject({ startLine: 4, endLine: 6, totalLines: 10, truncated: true, next: "Read on with offset 7." });
    expect(readNumbered(file, 8, 50)).toMatchObject({ endLine: 10, truncated: false, next: null });
  });

  test("a long file stops at the line limit; a file of very long lines stops at the size limit", () => {
    const many = Array.from({ length: 2500 }, (_, i) => `l${i}`).join("\n");
    expect(readNumbered(many)).toMatchObject({ endLine: 2000, totalLines: 2500, truncated: true });

    const wide = Array.from({ length: 100 }, () => "x".repeat(1500)).join("\n");
    const read = readNumbered(wide);
    expect(read.endLine).toBeLessThan(100);
    expect(read.content.length).toBeLessThan(62_000);
    expect(read.next).toBe(`Read on with offset ${read.endLine + 1}.`);
  });

  test("an overlong line is cut and says so; windows line endings do not leak", () => {
    const read = readNumbered(`${"y".repeat(2500)}\r\nshort\r\n`);
    expect(read.content).toContain("… (line cut, 2500 chars)");
    expect(read.content.endsWith("2\tshort")).toBe(true);
  });

  test("binary content and an offset past the end are refused; an empty file is empty", () => {
    expect(() => readNumbered("a\0b")).toThrow("binary file");
    expect(() => readNumbered("one\ntwo", 5)).toThrow("only 2 line(s)");
    expect(readNumbered("")).toMatchObject({ content: "", totalLines: 0, endLine: 0, truncated: false });
  });
});

describe("replaceExact", () => {
  test("a unique match is replaced", () => {
    expect(replaceExact("a = 1\nb = 2\n", "b = 2", "b = 3", false)).toEqual({ result: "a = 1\nb = 3\n", replacements: 1, matched: "exact" });
  });

  test("an ambiguous match is refused unless every occurrence is wanted", () => {
    expect(() => replaceExact("x\nx\n", "x", "y", false)).toThrow("found 2 matches");
    expect(replaceExact("x\nx\n", "x", "y", true)).toMatchObject({ result: "y\ny\n", replacements: 2 });
  });

  test("no match, an empty search and a no-op are each refused with a reason", () => {
    expect(() => replaceExact("abc", "zzz", "y", false)).toThrow("not found");
    expect(() => replaceExact("abc", "", "y", false)).toThrow("empty");
    expect(() => replaceExact("abc", "b", "b", false)).toThrow("nothing would change");
  });

  test("replacement text is taken literally", () => {
    expect(replaceExact("price = X", "X", "$& and $1", false).result).toBe("price = $& and $1");
  });

  test("a file with windows line endings keeps them, whatever the model typed", () => {
    const file = "first\r\nsecond\r\nthird\r\n";

    const edit = replaceExact(file, "first\nsecond", "one\ntwo\nthree", false);

    expect(edit).toEqual({ result: "one\r\ntwo\r\nthree\r\nthird\r\n", replacements: 1, matched: "line-endings" });
    // Also for an exact single-line match: the new text is written with the file's endings.
    expect(replaceExact(file, "third", "3a\n3b", false).result).toBe("first\r\nsecond\r\n3a\r\n3b\r\n");
  });

  test("tidied typography still finds the code, and only the matched range changes", () => {
    const file = 'say("it’s “fine”")  # a — b\nother = "it\'s"\n';

    const edit = replaceExact(file, `say("it's "fine"")`, "say(MESSAGE)", false);

    expect(edit.matched).toBe("typography");
    expect(edit.result).toBe('say(MESSAGE)  # a — b\nother = "it\'s"\n');
  });

  test("whitespace at the ends of lines does not prevent a match", () => {
    const file = "def run():   \n    return 1\t\n\nprint(run())\n";

    const edit = replaceExact(file, "def run():\n    return 1", "def run():\n    return 2", false);

    expect(edit).toEqual({ result: "def run():\n    return 2\n\nprint(run())\n", replacements: 1, matched: "trailing-whitespace" });
  });

  test("near-misses come together: other line endings, tidied quotes and trailing spaces at once", () => {
    const file = "def run():   \r\n    return \u201Chi\u201D\r\nprint(run())\r\n";

    const edit = replaceExact(file, 'def run():\n    return "hi"', 'def run():\n    return "bye"', false);

    expect(edit.result).toBe('def run():\r\n    return "bye"\r\nprint(run())\r\n');
    expect(edit.matched).toBe("trailing-whitespace");
  });

  test("a near-miss that matches in two places is as ambiguous as an exact one", () => {
    expect(() => replaceExact("a  \nb\na  \nb\n", "a\nb", "c", false)).toThrow("found 2 matches");
    expect(replaceExact("a  \nb\na  \nb\n", "a\nb", "c", true)).toMatchObject({ result: "c\nc\n", replacements: 2 });
  });

  test("leading whitespace is never ignored: indentation is part of the code", () => {
    expect(() => replaceExact("    return 1\n", "return 2", "x", false)).toThrow("not found");
    expect(() => replaceExact("    a\n    b\n", "a\n  b", "x", false)).toThrow("not found");
  });
});

describe("changeSummary", () => {
  test("shows the lines that went and the lines that came, with where", () => {
    const summary = changeSummary("a\nb\nc\nd\n", "a\nB1\nB2\nc\nd\n");

    expect(summary).toEqual({ diff: "@@ line 2 @@\n-b\n+B1\n+B2", added: 2, removed: 1 });
  });

  test("a pure insertion and a pure removal", () => {
    expect(changeSummary("a\nc\n", "a\nb\nc\n")).toMatchObject({ diff: "@@ line 2 @@\n+b", added: 1, removed: 0 });
    expect(changeSummary("a\nb\nc\n", "a\nc\n")).toMatchObject({ diff: "@@ line 2 @@\n-b", added: 0, removed: 1 });
  });

  test("a large change is cut, and says how much is left out", () => {
    const before = Array.from({ length: 100 }, (_, i) => `old ${i}`).join("\n");
    const after = Array.from({ length: 100 }, (_, i) => `new ${i}`).join("\n");

    const summary = changeSummary(before, after);

    expect(summary.diff.split("\n")).toHaveLength(61);
    expect(summary.diff).toContain("… 141 more line(s)");
    expect(summary).toMatchObject({ added: 100, removed: 100 });
  });
});

describe("fitOutput", () => {
  test("short output is returned untouched and nothing is saved", () => {
    let saved = 0;
    expect(fitOutput("all good\n", () => { saved++; return "/x"; }, 100)).toBe("all good\n");
    expect(saved).toBe(0);
  });

  test("long output keeps its beginning and its end, and the whole of it goes to a file", () => {
    const text = `START${"-".repeat(1000)}END`;
    let saved = "";

    const shown = fitOutput(text, (full) => { saved = full; return "/tmp/out.txt"; }, 100);

    expect(saved).toBe(text);
    expect(shown.startsWith("START")).toBe(true);
    expect(shown.endsWith("END")).toBe(true);
    expect(shown).toContain("showing the first 25 and the last 75 of 1008");
    expect(shown).toContain("Full output saved to /tmp/out.txt");
    expect(shown.length).toBeLessThan(400);
  });
});

describe("parseGrepLine", () => {
  test("reads file, line and text; a match in a file with windows line endings is not lost", () => {
    expect(parseGrepLine("/w/app.py:12:    return x")).toEqual({ file: "/w/app.py", line: 12, content: "    return x" });
    expect(parseGrepLine("/w/app.py:12:    return x\r")).toEqual({ file: "/w/app.py", line: 12, content: "    return x" });
    expect(parseGrepLine("/w/a.py:3:url = \"http://h:80\"")).toEqual({ file: "/w/a.py", line: 3, content: 'url = "http://h:80"' });
    expect(parseGrepLine("Binary file ./x matches")).toBeNull();
  });
});
