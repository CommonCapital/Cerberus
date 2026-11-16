import { afterEach, beforeEach, expect, mock, test } from "bun:test";
import { useEffect } from "react";
import { testRender } from "@opentui/react/test-utils";
import { createMemoryRouter, RouterProvider } from "react-router";
import type { MemoryEntry } from "../../lib/cerberus/api";

// The dialog is tested inside the real layout, opened as a command would open it,
// with the API replaced by an in-memory list.

let stored: MemoryEntry[] = [];
const calls: string[] = [];

const entry = (id: string, extra: Partial<MemoryEntry>): MemoryEntry => ({
  id, kind: "note", source: "operator", text: id, engagementId: null, targetPath: null, jobId: null,
  createdAt: "2026-11-12T10:00:00Z", ...extra,
});

mock.module("../../lib/cerberus/api", () => ({
  cerberus: {
    memory: async () => [...stored],
    remember: async (kind: "instruction" | "note", text: string) => {
      calls.push(`remember ${kind}: ${text}`);
      const created = entry(`new-${stored.length}`, { kind, text });
      stored.push(created);
      return created;
    },
    forget: async (id: string) => {
      calls.push(`forget ${id}`);
      stored = stored.filter((e) => e.id !== id);
      return null;
    },
  },
}));

const { MemoryDialogContent, describeEntry } = await import("./memory-dialog");
const { RootLayout } = await import("../../layouts/root-layout");
const { useDialog } = await import("../../providers/dialog");

function Opener() {
  const { open } = useDialog();
  useEffect(() => {
    open({ title: "Memory", children: <MemoryDialogContent /> });
  }, [open]);
  return <text>HOME</text>;
}

async function screen() {
  const router = createMemoryRouter([{ path: "/", element: <RootLayout />, children: [{ index: true, element: <Opener /> }] }]);
  const rendered = await testRender(<RouterProvider router={router} />, { width: 110, height: 30 });
  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
    await rendered.renderOnce();
  };
  await settle();
  await settle();
  return { ...rendered, settle };
}

let current: Awaited<ReturnType<typeof screen>> | null = null;
beforeEach(() => {
  calls.length = 0;
  stored = [
    entry("m1", { kind: "instruction", text: "Report in English." }),
    entry("m2", { text: "Auth lives in core/auth.", targetPath: "svc" }),
    entry("m3", { source: "agent", text: "Tests are under spec/.", targetPath: "svc", jobId: "job-1" }),
  ];
});
afterEach(() => {
  current?.renderer.destroy();
  current = null;
});

test("entries say what they are and who wrote them", () => {
  expect(describeEntry(stored[0]!)).toBe("instruction");
  expect(describeEntry(stored[1]!)).toBe("note · svc");
  expect(describeEntry(stored[2]!)).toBe("note by a model · svc");
});

test("the dialog lists what is remembered", async () => {
  const ui = (current = await screen());
  const text = ui.captureCharFrame();

  expect(text).toContain("Report in English.");
  expect(text).toContain("Auth lives in core/auth.");
  expect(text).toContain("note by a model · svc");
  expect(text).toContain("enter forget the highlighted entry");
});

test("typing offers to save it as a note or an instruction, and enter saves the chosen one", async () => {
  const ui = (current = await screen());

  for (const key of "be brief") ui.mockInput.pressKey(key);
  await ui.settle();
  expect(ui.captureCharFrame()).toContain("Save as an instruction");
  expect(ui.captureCharFrame()).toContain("always followed");
  expect(ui.captureCharFrame()).not.toContain("Auth lives in core/auth.");

  ui.mockInput.pressArrow("down");
  await ui.settle();
  ui.mockInput.pressEnter();
  await ui.settle();

  expect(calls).toEqual(["remember instruction: be brief"]);
  // Back to the list, with the new entry in it and the field empty.
  expect(ui.captureCharFrame()).toContain("be brief");
  expect(ui.captureCharFrame()).toContain("Auth lives in core/auth.");
  expect(ui.captureCharFrame()).not.toContain("Save as a note");
});

test("with nothing typed, enter forgets the highlighted entry", async () => {
  const ui = (current = await screen());

  ui.mockInput.pressArrow("down");
  ui.mockInput.pressArrow("down");
  await ui.settle();
  ui.mockInput.pressEnter();
  await ui.settle();

  expect(calls).toEqual(["forget m3"]);
  expect(ui.captureCharFrame()).not.toContain("note by a model");   // its row is gone
  expect(ui.captureCharFrame()).toContain("Forgotten: Tests are under spec/.");
  expect(ui.captureCharFrame()).toContain("Report in English.");
});
