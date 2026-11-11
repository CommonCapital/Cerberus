import { afterEach, beforeEach, expect, mock, test } from "bun:test";
import { testRender } from "@opentui/react/test-utils";
import { createMemoryRouter, RouterProvider } from "react-router";
import type { Assessment as AssessmentModel, AssessmentControl } from "../lib/cerberus/api";

// The screen with the API replaced: the test holds the assessment and sees what the operator did.

const calls: string[] = [];
let stored: AssessmentModel;

const control = (controlId: string, answer: string | null): AssessmentControl => ({
  controlId, title: `Control ${controlId}`, section: "S", verdict: "NotAssessed", rationale: "Needs manual review.",
  response: null,
  suggestion: answer ? { controlId, suggestion: answer, reasoning: "Because.", evidence: [], model: "llama3.1" } : null,
});

mock.module("../lib/cerberus/api", () => ({
  cerberus: {
    assessment: async () => structuredClone(stored),
    submitJob: async (job: { type: string; assessmentId?: string; model?: string }) => {
      calls.push(`submit ${job.type} ${job.assessmentId} ${job.model}`);
      return { id: "job-9" };
    },
    acceptSuggestion: async (id: string, controlId: string, extra: { actionPlan?: string } = {}) => {
      calls.push(`accept ${id} ${controlId}${extra.actionPlan ? ` plan=${extra.actionPlan}` : ""}`);
      const target = stored.controls.find((c) => c.controlId === controlId)!;
      target.response = { verdict: target.suggestion!.suggestion, justification: "Accepted.", actionPlan: extra.actionPlan ?? null, timeLimitDays: null };
    },
    dismissSuggestion: async (id: string, controlId: string) => {
      calls.push(`dismiss ${id} ${controlId}`);
      stored.controls.find((c) => c.controlId === controlId)!.suggestion = null;
      return null;
    },
  },
}));

const { Assessment } = await import("./assessment");
const { RootLayout } = await import("../layouts/root-layout");

async function screen() {
  const router = createMemoryRouter(
    [{
      path: "/",
      element: <RootLayout />,
      children: [
        { index: true, element: <text>HOME SCREEN</text> },
        { path: "assessments/:id", element: <Assessment /> },
        { path: "runs/:id", element: <text>RUN SCREEN</text> },
      ],
    }],
    { initialEntries: ["/assessments/a-1"] },
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
  stored = {
    id: "a-1", catalogId: "owasp-top10", targetPath: "svc", createdAt: "2026-11-12T10:00:00Z",
    summary: { totalControls: 4, compliant: 0, nonCompliant: 0, notAssessed: 4, score: null },
    effective: { compliant: 0, partiallyCompliant: 0, nonCompliant: 0, notApplicable: 0, unanswered: 4, operatorAnswered: 0 },
    controls: [control("A01", "Not applicable"), control("A04", "Cannot tell"), control("A07", "Non-compliant"), control("A09", null)],
  };
});
afterEach(() => {
  current?.renderer.destroy();
  current = null;
});

test("a accepts the highlighted suggestion and the control shows the operator's answer", async () => {
  const ui = (current = await screen());
  expect(ui.captureCharFrame()).toContain("a accept suggestion");

  ui.mockInput.pressKey("a");
  await ui.settle();
  await ui.settle();

  expect(calls).toEqual(["accept a-1 A01"]);
  expect(ui.captureCharFrame()).toContain("Your answer: Not applicable");
});

test("what the model could not tell cannot be accepted, only dismissed", async () => {
  const ui = (current = await screen());
  ui.mockInput.pressArrow("down");
  await ui.settle();
  expect(ui.captureCharFrame()).toContain("› A04");
  expect(ui.captureCharFrame()).not.toContain("accept suggestion");

  ui.mockInput.pressKey("a");
  await ui.settle();
  expect(calls).toEqual([]);

  ui.mockInput.pressKey("x");
  await ui.settle();
  await ui.settle();
  expect(calls).toEqual(["dismiss a-1 A04"]);
  expect(ui.captureCharFrame()).not.toContain("Suggested by llama3.1: Cannot tell");
});

test("a non-compliant suggestion asks for an action plan before it is accepted", async () => {
  const ui = (current = await screen());
  ui.mockInput.pressArrow("down");
  ui.mockInput.pressArrow("down");
  await ui.settle();

  ui.mockInput.pressKey("a");
  await ui.settle();
  expect(ui.captureCharFrame()).toContain("needs an action plan");
  expect(calls).toEqual([]);

  ui.mockInput.pressEnter();   // nothing written: not accepted
  await ui.settle();
  expect(calls).toEqual([]);

  // Letters that are shortcuts elsewhere on this screen are just text here.
  ui.type("fix auth exe");
  await ui.settle();
  ui.mockInput.pressEnter();
  await ui.settle();
  await ui.settle();

  expect(calls).toEqual(["accept a-1 A07 plan=fix auth exe"]);
  expect(ui.captureCharFrame()).toContain("Plan: fix auth exe");
});

test("e asks the current model for evidence and opens that run", async () => {
  const ui = (current = await screen());

  ui.mockInput.pressKey("e");
  await ui.settle();
  await ui.settle();

  expect(calls).toHaveLength(1);
  expect(calls[0]).toStartWith("submit evidence a-1 ");
  expect(ui.captureCharFrame()).toContain("RUN SCREEN");
});

test("a control without a suggestion offers neither accept nor dismiss", async () => {
  const ui = (current = await screen());
  for (let i = 0; i < 5; i++) ui.mockInput.pressArrow("down");   // stops at the last control
  await ui.settle();

  expect(ui.captureCharFrame()).toContain("› A09");
  expect(ui.captureCharFrame()).not.toContain("dismiss it");
  ui.mockInput.pressKey("x");
  ui.mockInput.pressKey("a");
  await ui.settle();
  expect(calls).toEqual([]);
});
