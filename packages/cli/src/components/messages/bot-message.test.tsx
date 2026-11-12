import { afterEach, expect, test } from "bun:test";
import { testRender } from "@opentui/react/test-utils";
import { ThemeProvider } from "../../providers/theme";
import { setToolCallsExpanded } from "../../lib/tool-fold-state";
import type { Message } from "../../hooks/use-chat";
import { BotMessage } from "./bot-message";

type Parts = Message["parts"];

async function screen(parts: unknown[], streaming = false) {
  const rendered = await testRender(
    <ThemeProvider>
      <BotMessage parts={parts as Parts} model="llama3.1" mode="BUILD" streaming={streaming} />
    </ThemeProvider>,
    { width: 90, height: 40 },
  );
  // Markdown is laid out off the main thread: give it a moment before looking.
  const settle = async (ms = 400) => {
    await new Promise((resolve) => setTimeout(resolve, ms));
    await rendered.renderOnce();
  };
  await settle();
  const lines = () => rendered.captureCharFrame().split("\n").map((line) => line.trimEnd());
  return { ...rendered, settle, lines, text: () => lines().join("\n") };
}

let current: Awaited<ReturnType<typeof screen>> | null = null;
afterEach(() => {
  current?.renderer.destroy();
  current = null;
  setToolCallsExpanded(false);
});

const tool = (name: string, id: string, input: unknown, output: unknown = {}, state = "output-available") => ({
  type: `tool-${name}`, toolCallId: id, state, input, ...(state === "output-available" ? { output } : {}),
});

test("a reply's markdown is shown as what it means, not as its markers", async () => {
  const ui = (current = await screen([
    {
      type: "text",
      text: [
        "## What I found",
        "",
        "The **loader** calls `pickle.loads` on *untrusted* input.",
        "",
        "- first point",
        "- second point",
        "",
        "```python",
        "def load(blob):",
        "    return pickle.loads(blob)",
        "```",
      ].join("\n"),
    },
  ]));

  const text = ui.text();
  if (process.env.SHOW_FRAMES) console.log(text);
  expect(text).toContain("What I found");
  expect(text).not.toContain("## What I found");
  expect(text).toContain("The loader calls pickle.loads on untrusted input.");
  expect(text).toContain("- first point");
  expect(text).toContain("def load(blob):");
  expect(text).toContain("    return pickle.loads(blob)");
  expect(text).not.toContain("```");
  expect(text).toContain("Build");   // the reply's footer is still there
});

test("where a link goes is shown beside its words", async () => {
  const ui = (current = await screen([{ type: "text", text: "See [the advisory](https://example.test/advisory/7)." }]));

  const text = ui.text();
  expect(text).toContain("the advisory");
  expect(text).toContain("https://example.test/advisory/7");
});

test("a table in a reply is drawn as a table", async () => {
  const ui = (current = await screen([{ type: "text", text: "| File | Line |\n|---|---|\n| loader.py | 7 |\n" }]));

  const text = ui.text();
  expect(text).toContain("│");
  expect(text).toMatch(/File\s*│\s*Line/);
  expect(text).toMatch(/loader\.py\s*│\s*7/);
  expect(text).not.toContain("|---|");
});

test("a reply that is still arriving shows what has arrived", async () => {
  const ui = (current = await screen([{ type: "text", text: "Reading the **loa" }], true));

  expect(ui.text()).toContain("Reading the");
});

const exploring = [
  { type: "text", text: "Let me look." },
  tool("listDirectory", "t1", { path: "svc" }),
  tool("readFile", "t2", { path: "svc/loader.py" }),
  tool("grep", "t3", { pattern: "pickle" }),
  tool("readFile", "t4", { path: "svc/app.py" }),
  tool("editFile", "t5", { path: "svc/loader.py", oldString: "pickle", newString: "json" }, { path: "svc/loader.py", replacements: 1, diff: "-import pickle\n+import json" }),
  { type: "text", text: "Replaced it." },
];

test("a run of reads and searches is one line; the edit that followed is shown in full", async () => {
  const ui = (current = await screen(exploring));

  const text = ui.text();
  if (process.env.SHOW_FRAMES) console.log(text);
  expect(text).toContain("Explored: 2 reads, 1 search, 1 listing · ctrl+o shows each");
  expect(text).not.toContain("Read File:");
  expect(text).not.toContain("svc/app.py");
  expect(text).toContain("Edit File: svc/loader.py");
  expect(text).toContain("Let me look.");
  expect(text).toContain("Replaced it.");
});

test("expanded, every call of the run is shown again", async () => {
  const ui = (current = await screen(exploring));

  setToolCallsExpanded(true);
  await ui.settle();

  const text = ui.text();
  expect(text).not.toContain("Explored:");
  expect(text).toContain("List Directory: svc");
  expect(text).toContain("Read File: svc/loader.py");
  expect(text).toContain("Grep: pickle");
  expect(text).toContain("Read File: svc/app.py");

  setToolCallsExpanded(false);
  await ui.settle();
  expect(ui.text()).toContain("Explored: 2 reads, 1 search, 1 listing");
});

test("a call still running is shown under the run it will join", async () => {
  const ui = (current = await screen(
    [
      tool("readFile", "t1", { path: "a.py" }),
      tool("readFile", "t2", { path: "b.py" }),
      tool("readFile", "t3", { path: "c.py" }),
      tool("readFile", "t4", { path: "d.py" }, undefined, "input-available"),
    ],
    true,
  ));

  const text = ui.text();
  expect(text).toContain("Explored: 3 reads");
  expect(text).toContain("Read File: d.py …");
});
