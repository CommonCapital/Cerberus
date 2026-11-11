import { expect, test } from "bun:test";
import { testRender } from "@opentui/react/test-utils";
import { RunView } from "./run-view";
import { ThemeProvider } from "../providers/theme";
import { applyEvents, emptyRun } from "../lib/cerberus/run-state";
import type { Job, RunEvent } from "../lib/cerberus/api";

const job = (state: Job["state"], extra: Partial<Job> = {}): Job => ({
  id: "job-1", type: "scan", state, tier: "T0", engagementId: null, targetPath: "services/api",
  resultId: null, approvalId: null, error: null,
  createdAt: "2026-11-11T10:00:00Z", startedAt: null, finishedAt: null, ...extra,
});

function view(events: [string, unknown][]) {
  const stream: RunEvent[] = events.map(([type, data], index) => ({
    jobId: "job-1", sequence: index + 1, type, data, createdAt: "2026-11-11T10:00:00Z",
  }));
  return applyEvents(emptyRun(), stream).view;
}

async function frame(model: ReturnType<typeof view>, selectedOption = 0) {
  const { renderOnce, captureCharFrame, renderer } = await testRender(
    <ThemeProvider>
      <RunView view={model} selectedOption={selectedOption} />
    </ThemeProvider>,
    { width: 100, height: 30 },
  );
  await renderOnce();
  const text = captureCharFrame();
  renderer.destroy();
  return text;
}

const running: [string, unknown][] = [
  ["state", job("Queued")],
  ["state", job("Running")],
  ["step", { id: "static", title: "Static analysis", status: "running" }],
  ["finding", { id: "injection.os-command:app.py:9", title: "Command passed to a system shell", severity: "High", category: "injection", file: "app.py", line: 9 }],
  ["finding", { id: "crypto.weak-hash:tests/t.py:3", title: "Weak hash algorithm (MD5/SHA-1)", severity: "Medium", category: "crypto", file: "tests/t.py", line: 3, context: "test" }],
  ["step", { id: "dependencies", title: "Dependency check", status: "skipped" }],
  ["step", { id: "store", title: "Store results", status: "completed" }],
  ["step", { id: "static", title: "Static analysis", status: "running" }],
  ["progress", { done: 12, total: 12, message: "12 files scanned, 2 findings." }],
];

test("a running scan shows its state, task list, progress and findings", async () => {
  const text = await frame(view(running));
  if (process.env.SHOW_FRAMES) console.log(text);

  expect(text).toContain("Scan · services/api");
  expect(text).toContain("● Running");
  expect(text).toContain("▶ Static analysis");
  expect(text).toContain("– Dependency check (skipped)");
  expect(text).toContain("✔ Store results");
  expect(text).toContain("12 files scanned, 2 findings.");
  expect(text).toContain("Findings (2)");
  expect(text).toContain("High 1 · Medium 1");
  expect(text).toContain("HIGH     Command passed to a system shell app.py:9");
  expect(text).toContain("tests/t.py:3 [test]");
});

test("a question is shown with its options and the highlighted one", async () => {
  const asked = view([
    ["state", job("Running")],
    ["state", job("AwaitingInput")],
    ["question", {
      id: "q-1", jobId: "job-1", text: "Honour the target's own exclusions?",
      options: [
        { label: "Honour them", description: "Right for code you maintain." },
        { label: "Scan everything", description: "Right for code you do not trust." },
      ],
      multiSelect: false, allowText: false, status: "Pending", answer: null,
    }],
  ]);

  const first = await frame(asked, 0);
  if (process.env.SHOW_FRAMES) console.log(first);
  expect(first).toContain("● Waiting for your answer");
  expect(first).toContain("Honour the target's own exclusions?");
  expect(first).toContain("› Honour them");
  expect(first).toContain("  Scan everything");
  expect(first).toContain("Right for code you do not trust.");
  expect(first).toContain("enter answer");

  const second = await frame(asked, 1);
  expect(second).toContain("› Scan everything");
  expect(second).not.toContain("› Honour them");
});

test("an approval request shows what is being asked and how to decide", async () => {
  const text = await frame(view([
    ["state", job("AwaitingApproval", { type: "triage" })],
    ["approval_requested", {
      id: "a-1", jobId: "job-1", kind: "send-code-to-cloud-model",
      title: "Send source code to Anthropic (claude-haiku-4-5) for triage",
      detail: "Would send the code around 3 finding(s) in 2 file(s). Nothing is sent until you approve.",
      status: "Pending", note: null,
    }],
  ]));
  if (process.env.SHOW_FRAMES) console.log(text);

  expect(text).toContain("Finding triage · services/api");
  expect(text).toContain("● Waiting for your approval");
  expect(text).toContain("Approval needed");
  expect(text).toContain("Send source code to Anthropic (claude-haiku-4-5) for triage");
  expect(text).toContain("Nothing is sent until you approve.");
  expect(text).toContain("a approve d deny");
});

test("a finished run shows the outcome, the answers given and the result", async () => {
  const text = await frame(view([
    ...running,
    ["question", { id: "q-1", jobId: "job-1", text: "Honour them?", options: [], multiSelect: false, allowText: true, status: "Pending", answer: null }],
    ["question_resolved", { id: "q-1", jobId: "job-1", text: "Honour them?", options: [], multiSelect: false, allowText: true, status: "Answered", answer: { selected: ["Scan everything"], text: null } }],
    ["state", job("Succeeded", { resultId: "scan-42" })],
  ]));
  if (process.env.SHOW_FRAMES) console.log(text);

  expect(text).toContain("● Succeeded");
  expect(text).toContain("↳ Honour them? → Scan everything");
  expect(text).toContain("Result: scan scan-42");
  expect(text).not.toContain("Question");
});

test("a failed run shows why", async () => {
  const text = await frame(view([
    ["state", job("Running")],
    ["state", job("Failed", { error: "No longer authorized: Engagement is aborted; no further runs are allowed." })],
  ]));

  expect(text).toContain("● Failed");
  expect(text).toContain("No longer authorized: Engagement is aborted");
});

test("model output and verdicts are shown during triage", async () => {
  const text = await frame(view([
    ["state", job("Running", { type: "triage" })],
    ["triage", { findingId: "deserialization.unsafe:svc/loader.py:2", verdict: "TruePositive", reasoning: "pickle.loads runs on data from outside.", model: "m" }],
    ["text", { channel: "crypto.weak-hash:a.py:3", text: '{"verdict":"false_positive","reasoning":"Only a cache key' }],
  ]));
  if (process.env.SHOW_FRAMES) console.log(text);

  expect(text).toContain("Model review (1)");
  expect(text).toContain("REAL svc/loader.py:2");
  expect(text).toContain("pickle.loads runs on data from outside.");
  expect(text).toContain("Only a cache key");
});

test("before the first event arrives it says it is loading", async () => {
  expect(await frame(emptyRun())).toContain("Loading the run…");
});
