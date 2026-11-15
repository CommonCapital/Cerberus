import { afterEach, beforeEach, expect, mock, test } from "bun:test";
import { testRender } from "@opentui/react/test-utils";
import { createMemoryRouter, RouterProvider } from "react-router";
import type { ScanDetail, ScanFinding } from "../lib/cerberus/api";

// The screen with the API replaced: the test holds the scan and sees what the operator decided.

const calls: string[] = [];
let stored: ScanDetail;

const finding = (id: string, severity: ScanFinding["severity"]): ScanFinding => ({
  id, title: `Problem ${id}`, severity, category: "injection", file: "app.py", line: 1, evidence: "os.system(cmd)", decision: null,
});

mock.module("../lib/cerberus/api", () => ({
  cerberus: {
    scan: async () => structuredClone(stored),
    decideFinding: async (scanId: string, findingId: string, status: "accepted-risk" | "false-positive", note: string) => {
      calls.push(`decide ${scanId} ${findingId} ${status}: ${note}`);
      stored.findings.find((f) => f.id === findingId)!.decision = { id: `d-${findingId}`, status, note, decidedAt: "2026-11-12T10:00:00Z" };
    },
    withdrawDecision: async (decisionId: string) => {
      calls.push(`withdraw ${decisionId}`);
      for (const f of stored.findings) if (f.decision?.id === decisionId) f.decision = null;
      return null;
    },
  },
}));

const { Scan } = await import("./scan");
const { RootLayout } = await import("../layouts/root-layout");

async function screen() {
  const router = createMemoryRouter(
    [{
      path: "/",
      element: <RootLayout />,
      children: [
        { index: true, element: <text>HOME SCREEN</text> },
        { path: "scans/:id", element: <Scan /> },
      ],
    }],
    { initialEntries: ["/scans/s-1"] },
  );
  const rendered = await testRender(<RouterProvider router={router} />, { width: 120, height: 34 });
  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
    await rendered.renderOnce();
  };
  await settle();
  await settle();
  return { ...rendered, settle, type: (text: string) => { for (const key of text) rendered.mockInput.pressKey(key); } };
}

let current: Awaited<ReturnType<typeof screen>> | null = null;
beforeEach(() => {
  calls.length = 0;
  stored = { id: "s-1", targetPath: "svc", findingCount: 2, createdAt: "2026-11-12T10:00:00Z", findings: [finding("low", "Low"), finding("high", "High")] };
});
afterEach(() => {
  current?.renderer.destroy();
  current = null;
});

test("r accepts the risk of the highlighted finding once a reason is given; the finding keeps its place", async () => {
  const ui = (current = await screen());
  expect(ui.captureCharFrame()).toMatch(/› HIGH +Problem high/);   // most severe first

  ui.mockInput.pressKey("r");
  await ui.settle();
  expect(ui.captureCharFrame()).toContain('Why is "Problem high" a risk you accept?');

  ui.mockInput.pressEnter();   // no reason: nothing is recorded
  await ui.settle();
  expect(calls).toEqual([]);

  // Letters that are shortcuts on this screen are just text while writing the reason.
  ui.type("proxy drops it");
  await ui.settle();
  ui.mockInput.pressEnter();
  await ui.settle();
  await ui.settle();

  expect(calls).toEqual(["decide s-1 high accepted-risk: proxy drops it"]);
  const text = ui.captureCharFrame();
  expect(text).toContain("You decided: accepted risk — proxy drops it");
  expect(text).toMatch(/› HIGH +Problem high/);          // still highlighted, still first
  expect(text).toContain("1 open · 1 decided");
});

test("p marks a false positive, and x withdraws a decision", async () => {
  const ui = (current = await screen());
  ui.mockInput.pressArrow("down");
  await ui.settle();

  ui.mockInput.pressKey("p");
  await ui.settle();
  expect(ui.captureCharFrame()).toContain('Why is "Problem low" a false positive?');
  ui.type("never called");
  await ui.settle();
  ui.mockInput.pressEnter();
  await ui.settle();
  await ui.settle();
  expect(calls).toEqual(["decide s-1 low false-positive: never called"]);
  expect(ui.captureCharFrame()).toContain("x withdraw the decision");

  ui.mockInput.pressKey("x");
  await ui.settle();
  await ui.settle();
  expect(calls).toEqual(["decide s-1 low false-positive: never called", "withdraw d-low"]);
  expect(ui.captureCharFrame()).toContain("2 open · 0 decided");
});

test("escape cancels a decision in progress without recording anything; x does nothing on an open finding", async () => {
  const ui = (current = await screen());

  ui.mockInput.pressKey("x");
  ui.mockInput.pressKey("r");
  await ui.settle();
  ui.type("half a reason");
  ui.mockInput.pressEscape();
  await new Promise((resolve) => setTimeout(resolve, 250));
  await ui.settle();

  expect(calls).toEqual([]);
  expect(ui.captureCharFrame()).not.toContain("Why is");
  expect(ui.captureCharFrame()).toContain("Findings · svc");   // still on the screen
});
