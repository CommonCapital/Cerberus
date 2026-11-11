import { afterEach, beforeEach, expect, mock, test } from "bun:test";
import { testRender } from "@opentui/react/test-utils";
import { createMemoryRouter, RouterProvider } from "react-router";
import type { Job, RunEvent } from "../lib/cerberus/api";
import { applyEvents, emptyRun, type RunView } from "../lib/cerberus/run-state";

// The screen is tested with the network layer replaced: `followRun` hands the
// test a way to push views, and the API records what the operator did.

const calls: string[] = [];
let push: (view: RunView) => void = () => {};
// One entry per mounted screen, so a test only looks at its own.
const followers: { stopped: boolean }[] = [];

mock.module("../lib/cerberus/follow", () => ({
  followRun: (_jobId: string, onChange: (view: RunView) => void) => {
    push = onChange;
    const follower = { stopped: false };
    followers.push(follower);
    return { stop: async () => { follower.stopped = true; } };
  },
}));

mock.module("../lib/cerberus/api", () => ({
  cerberus: {
    approve: async (id: string, options?: { remember?: boolean }) => {
      calls.push(options?.remember ? `approve+remember ${id}` : `approve ${id}`);
    },
    deny: async (id: string) => { calls.push(`deny ${id}`); },
    answer: async (id: string, selected: string[]) => { calls.push(`answer ${id} ${selected.join(",")}`); },
    cancelJob: async (id: string) => { calls.push(`cancel ${id}`); },
    sendMessage: async (id: string, text: string) => { calls.push(`message ${id} ${text}`); },
  },
}));

const { Run } = await import("./run");
const { useParams } = await import("react-router");

function AssessmentStub() {
  const { id } = useParams();
  return <text>ASSESSMENT {id}</text>;
}
const { RootLayout } = await import("../layouts/root-layout");

const job = (state: Job["state"]): Job => ({
  id: "job-1", type: "scan", state, tier: "T0", engagementId: null, targetPath: "svc",
  resultId: null, approvalId: null, error: null,
  createdAt: "2026-11-11T10:00:00Z", startedAt: null, finishedAt: null,
});

const view = (events: [string, unknown][]) =>
  applyEvents(emptyRun(), events.map(([type, data], index): RunEvent => ({
    jobId: "job-1", sequence: index + 1, type, data, createdAt: "2026-11-11T10:00:00Z",
  }))).view;

const question = {
  id: "q-1", jobId: "job-1", text: "Honour the target's own exclusions?",
  options: [{ label: "Honour them" }, { label: "Scan everything" }],
  multiSelect: false, allowText: false, status: "Pending", answer: null,
};

const approval = {
  id: "a-1", jobId: "job-1", kind: "send-code-to-cloud-model", title: "Send source code to Anthropic",
  detail: "1 finding in 1 file.", status: "Pending", note: null,
};

async function screen() {
  const router = createMemoryRouter(
    [{
      path: "/",
      element: <RootLayout />,
      children: [
        { index: true, element: <text>HOME SCREEN</text> },
        { path: "runs/:id", element: <Run /> },
        { path: "assessments/:id", element: <AssessmentStub /> },
      ],
    }],
    { initialEntries: ["/runs/job-1"] },
  );
  const rendered = await testRender(<RouterProvider router={router} />, { width: 100, height: 30 });
  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
    await rendered.renderOnce();
  };
  await settle();
  return { ...rendered, settle, show: async (v: RunView) => { push(v); await settle(); } };
}

beforeEach(() => {
  calls.length = 0;
});

let current: Awaited<ReturnType<typeof screen>> | null = null;
afterEach(() => {
  current?.renderer.destroy();
  current = null;
});

test("arrow keys move the highlight and enter sends the highlighted answer", async () => {
  const ui = (current = await screen());
  await ui.show(view([["state", job("AwaitingInput")], ["question", question]]));
  expect(ui.captureCharFrame()).toContain("› Honour them");

  ui.mockInput.pressArrow("down");
  await ui.settle();
  expect(ui.captureCharFrame()).toContain("› Scan everything");

  ui.mockInput.pressArrow("down");   // already at the last option: stays there
  ui.mockInput.pressEnter();
  await ui.settle();

  expect(calls).toEqual(["answer q-1 Scan everything"]);
});

test("a approves, r approves and remembers, d denies a pending approval", async () => {
  const ui = (current = await screen());
  await ui.show(view([["state", job("AwaitingApproval")], ["approval_requested", approval]]));
  expect(ui.captureCharFrame()).toContain("Approval needed");

  ui.mockInput.pressKey("a");
  await ui.settle();
  ui.mockInput.pressKey("r");
  await ui.settle();
  ui.mockInput.pressKey("d");
  await ui.settle();

  expect(calls).toEqual(["approve a-1", "approve+remember a-1", "deny a-1"]);
});

test("while a decision is pending, c does not cancel the run by accident", async () => {
  const ui = (current = await screen());
  await ui.show(view([["state", job("AwaitingApproval")], ["approval_requested", approval]]));

  ui.mockInput.pressKey("c");
  await ui.settle();

  expect(calls).toEqual([]);
});

test("c cancels a running job, and does nothing once it has finished", async () => {
  const ui = (current = await screen());
  await ui.show(view([["state", job("Running")]]));
  expect(ui.captureCharFrame()).toContain("c cancel run");

  ui.mockInput.pressKey("c");
  await ui.settle();
  expect(calls).toEqual(["cancel job-1"]);

  await ui.show(view([["state", job("Running")], ["state", job("Succeeded")]]));
  expect(ui.captureCharFrame()).not.toContain("cancel run");
  ui.mockInput.pressKey("c");
  await ui.settle();
  expect(calls).toEqual(["cancel job-1"]);
});

test("escape leaves the run and stops following it", async () => {
  const ui = (current = await screen());
  await ui.show(view([["state", job("Running")]]));
  const follower = followers[followers.length - 1]!;
  expect(follower.stopped).toBe(false);

  ui.mockInput.pressEscape();
  // A lone Escape is held back briefly by the input parser (it could start an escape sequence).
  await new Promise((resolve) => setTimeout(resolve, 250));
  await ui.settle();

  expect(ui.captureCharFrame()).toContain("HOME SCREEN");
  expect(follower.stopped).toBe(true);
});

test("m writes a message to a running agent; typing does not trigger other keys", async () => {
  const ui = (current = await screen());
  const agent = { ...job("Running"), type: "sast" };
  await ui.show(view([["state", agent], ["tool_call", { id: "c1", name: "listDirectory", input: {} }]]));
  expect(ui.captureCharFrame()).toContain("m message the agent");

  ui.mockInput.pressKey("m");
  await ui.settle();
  expect(ui.captureCharFrame()).toContain("Tell the agent what to do next");

  // "c" would cancel the run and "a" approve if the screen still listened to them.
  for (const key of "check auth") ui.mockInput.pressKey(key);
  await ui.settle();
  ui.mockInput.pressEnter();
  await ui.settle();

  expect(calls).toEqual(["message job-1 check auth"]);
  expect(ui.captureCharFrame()).not.toContain("Tell the agent what to do next");

  await ui.show(view([["state", agent], ["operator_message", { text: "check auth" }]]));
  expect(ui.captureCharFrame()).toContain("You told the agent: check auth");
});

test("a run without an agent, or one that has finished, takes no messages", async () => {
  const ui = (current = await screen());
  await ui.show(view([["state", job("Running")]]));
  expect(ui.captureCharFrame()).not.toContain("message the agent");
  ui.mockInput.pressKey("m");
  await ui.settle();
  expect(ui.captureCharFrame()).not.toContain("Tell the agent what to do next");

  await ui.show(view([["state", { ...job("Succeeded"), type: "sast" }]]));
  expect(ui.captureCharFrame()).not.toContain("message the agent");
});

test("a finished assessment run offers to open the assessment it produced", async () => {
  const ui = (current = await screen());
  await ui.show(view([["state", { ...job("Succeeded"), type: "assess", resultId: "assess-7" }]]));
  expect(ui.captureCharFrame()).toContain("o open the assessment");

  ui.mockInput.pressKey("o");
  await ui.settle();
  expect(ui.captureCharFrame()).toContain("ASSESSMENT assess-7");
});
