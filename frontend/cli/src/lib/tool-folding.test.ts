import { expect, test } from "bun:test";
import { describeRun, foldQuietRuns } from "./tool-folding";

const tool = (name: string, id: string, state = "output-available") => ({ type: `tool-${name}`, toolCallId: id, state });
const text = (value: string) => ({ type: "text", text: value });

const kinds = (shown: ReturnType<typeof foldQuietRuns>) =>
  shown.map((item) => (item.kind === "fold" ? `fold(${item.count}: ${item.summary})` : item.part.type));

test("a run of reads, searches and listings becomes one line that counts them", () => {
  const shown = foldQuietRuns(
    [text("Looking."), tool("listDirectory", "a"), tool("readFile", "b"), tool("grep", "c"), tool("readFile", "d"), tool("glob", "e"), text("Found it.")],
    false,
  );

  expect(kinds(shown)).toEqual(["text", "fold(5: 2 reads, 2 searches, 1 listing)", "text"]);
});

test("two calls are not worth folding", () => {
  const shown = foldQuietRuns([tool("readFile", "a"), tool("grep", "b"), text("Done.")], false);

  expect(kinds(shown)).toEqual(["tool-readFile", "tool-grep", "text"]);
});

test("whatever changes or runs something is always shown, and splits the runs around it", () => {
  const shown = foldQuietRuns(
    [
      tool("readFile", "a"), tool("readFile", "b"), tool("readFile", "c"),
      tool("editFile", "d"),
      tool("readFile", "e"), tool("grep", "f"),
      tool("bash", "g"),
      tool("updateTasks", "h"),
    ],
    false,
  );

  expect(kinds(shown)).toEqual(["fold(3: 3 reads)", "tool-editFile", "tool-readFile", "tool-grep", "tool-bash", "tool-updateTasks"]);
});

test("a call that failed, or is still going, is never folded away", () => {
  const shown = foldQuietRuns(
    [
      tool("readFile", "a"), tool("readFile", "b"), tool("readFile", "c"),
      tool("readFile", "d", "output-error"),
      tool("grep", "e"), tool("grep", "f"), tool("grep", "g", "input-available"),
    ],
    false,
  );

  expect(kinds(shown)).toEqual(["fold(3: 3 reads)", "tool-readFile", "tool-grep", "tool-grep", "tool-grep"]);
});

test("expanded, everything is shown as it is, in order", () => {
  const parts = [tool("readFile", "a"), tool("readFile", "b"), tool("readFile", "c"), text("Done.")];

  const shown = foldQuietRuns(parts, true);

  expect(shown.map((item) => (item.kind === "part" ? item.index : -1))).toEqual([0, 1, 2, 3]);
});

test("a fold keeps its identity while the run grows", () => {
  const three = foldQuietRuns([tool("readFile", "first"), tool("grep", "b"), tool("grep", "c")], false);
  const four = foldQuietRuns([tool("readFile", "first"), tool("grep", "b"), tool("grep", "c"), tool("grep", "d")], false);

  expect(three[0]).toMatchObject({ kind: "fold", key: "fold-first", count: 3 });
  expect(four[0]).toMatchObject({ kind: "fold", key: "fold-first", count: 4 });
});

test("server-run tools and dynamic tools are named the same way", () => {
  const shown = foldQuietRuns(
    [
      { type: "dynamic-tool", toolName: "searchCode", toolCallId: "a", state: "output-available" },
      tool("searchSkills", "b"),
      tool("loadSkill", "c"),
    ],
    false,
  );

  expect(kinds(shown)).toEqual(["fold(3: 2 searches, 1 skill)"]);
});

test("counts read naturally", () => {
  expect(describeRun(["readFile"])).toBe("1 read");
  expect(describeRun(["grep", "glob", "readFile", "listDirectory", "listDirectory", "loadSkill", "loadSkill"])).toBe("1 read, 2 searches, 2 listings, 2 skills");
});
