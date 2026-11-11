import { describe, expect, test } from "bun:test";
import type { Job, RunEvent } from "./api";
import { applyEvents, emptyRun, isFinished, isWaitingForOperator, severityCounts } from "./run-state";

const job = (state: Job["state"]): Job => ({
  id: "job-1",
  type: "scan",
  state,
  tier: "T0",
  engagementId: null,
  targetPath: "svc",
  resultId: state === "Succeeded" ? "scan-9" : null,
  approvalId: null,
  error: null,
  createdAt: "2026-11-11T10:00:00Z",
  startedAt: null,
  finishedAt: null,
});

let sequence = 0;
const event = (type: string, data: unknown, at?: number): RunEvent => ({
  jobId: "job-1",
  sequence: at ?? ++sequence,
  type,
  data,
  createdAt: "2026-11-11T10:00:00Z",
});

const finding = (id: string, severity: string) => ({
  id, title: "Weak hash", severity, category: "crypto", file: "a.py", line: 3,
});

const question = (status: string, answer: unknown = null) => ({
  id: "q-1", jobId: "job-1", text: "Honour the target's own exclusions?",
  options: [{ label: "Honour them" }, { label: "Scan everything" }],
  multiSelect: false, allowText: false, status, answer,
});

const approval = (status: string, note: string | null = null) => ({
  id: "a-1", jobId: "job-1", kind: "send-code-to-cloud-model", title: "Send source code to Anthropic",
  detail: "1 finding in 1 file", status, note,
});

describe("applyEvents", () => {
  test("builds the run from its story", () => {
    sequence = 0;
    const { view, gap } = applyEvents(emptyRun(), [
      event("state", job("Queued")),
      event("state", job("Running")),
      event("step", { id: "static", title: "Static analysis", status: "running" }),
      event("finding", finding("f1", "High")),
      event("finding", finding("f2", "Medium")),
      event("progress", { done: 1, total: 1, message: "1 files scanned, 2 findings." }),
      event("step", { id: "static", title: "Static analysis", status: "completed" }),
      event("step", { id: "dependencies", title: "Dependency check", status: "skipped" }),
      event("note", { message: "Something worth knowing." }),
      event("state", job("Succeeded")),
    ]);

    expect(gap).toBe(false);
    expect(view.lastSequence).toBe(10);
    expect(view.job?.state).toBe("Succeeded");
    expect(view.steps).toEqual([
      { id: "static", title: "Static analysis", status: "completed" },
      { id: "dependencies", title: "Dependency check", status: "skipped" },
    ]);
    expect(view.findings.map((f) => f.id)).toEqual(["f1", "f2"]);
    expect(view.progress?.message).toBe("1 files scanned, 2 findings.");
    expect(view.notes).toEqual(["Something worth knowing."]);
    expect(isFinished(view)).toBe(true);
  });

  test("progress is cleared when the next step starts", () => {
    sequence = 0;
    const view = applyEvents(emptyRun(), [
      event("step", { id: "static", title: "Static analysis", status: "running" }),
      event("progress", { done: 3, total: 3, message: "3 files scanned." }),
      event("step", { id: "static", title: "Static analysis", status: "completed" }),
      event("step", { id: "store", title: "Store results", status: "running" }),
    ]).view;

    expect(view.progress).toBeNull();
  });

  test("ignores events it has already seen", () => {
    sequence = 0;
    const events = [event("state", job("Running")), event("finding", finding("f1", "Low"))];
    const once = applyEvents(emptyRun(), events).view;
    const twice = applyEvents(once, events).view;

    expect(twice.findings).toHaveLength(1);
    expect(twice.lastSequence).toBe(2);
  });

  test("applies a batch in sequence order whatever order it arrives in", () => {
    const view = applyEvents(emptyRun(), [
      event("state", job("Succeeded"), 3),
      event("state", job("Queued"), 1),
      event("state", job("Running"), 2),
    ]).view;

    expect(view.job?.state).toBe("Succeeded");
  });

  test("reports a gap and waits for the missing events", () => {
    const first = applyEvents(emptyRun(), [event("state", job("Running"), 1)]).view;

    const skipped = applyEvents(first, [event("finding", finding("f3", "High"), 3)]);
    expect(skipped.gap).toBe(true);
    expect(skipped.view.lastSequence).toBe(1);
    expect(skipped.view.findings).toHaveLength(0);

    // Once the missing event is fetched, both apply.
    const filled = applyEvents(skipped.view, [
      event("finding", finding("f2", "Low"), 2),
      event("finding", finding("f3", "High"), 3),
    ]);
    expect(filled.gap).toBe(false);
    expect(filled.view.findings.map((f) => f.id)).toEqual(["f2", "f3"]);
  });

  test("joins model text per channel", () => {
    sequence = 0;
    const view = applyEvents(emptyRun(), [
      event("text", { channel: "f1", text: '{"verdict":' }),
      event("text", { channel: "f1", text: '"true_positive"}' }),
      event("text", { channel: "f2", text: "second" }),
    ]).view;

    expect(view.text).toEqual([
      { channel: "f1", text: '{"verdict":"true_positive"}' },
      { channel: "f2", text: "second" },
    ]);
  });

  test("a question is shown while pending and recorded once answered", () => {
    sequence = 0;
    const asked = applyEvents(emptyRun(), [
      event("state", job("AwaitingInput")),
      event("question", question("Pending")),
    ]).view;
    expect(asked.question?.id).toBe("q-1");
    expect(isWaitingForOperator(asked)).toBe(true);

    const answered = applyEvents(asked, [
      event("question_resolved", question("Answered", { selected: ["Scan everything"], text: null })),
      event("state", job("Running")),
    ]).view;
    expect(answered.question).toBeNull();
    expect(answered.resolved).toEqual(["Honour the target's own exclusions? → Scan everything"]);
    expect(isWaitingForOperator(answered)).toBe(false);
  });

  test("an expired question says so", () => {
    sequence = 0;
    const view = applyEvents(emptyRun(), [
      event("question", question("Pending")),
      event("question_resolved", question("Expired")),
    ]).view;

    expect(view.resolved).toEqual(["Honour the target's own exclusions? → expired"]);
  });

  test("an approval is shown while pending and recorded once decided", () => {
    sequence = 0;
    const waiting = applyEvents(emptyRun(), [
      event("state", job("AwaitingApproval")),
      event("approval_requested", approval("Pending")),
    ]).view;
    expect(waiting.approval?.title).toBe("Send source code to Anthropic");

    const denied = applyEvents(waiting, [
      event("approval_resolved", approval("Denied", "stays local")),
      event("state", job("Cancelled")),
    ]).view;
    expect(denied.approval).toBeNull();
    expect(denied.resolved).toEqual(["Denied: Send source code to Anthropic — stays local"]);
    expect(isFinished(denied)).toBe(true);
  });

  test("a run that stops waiting drops a prompt whose resolution it never saw", () => {
    sequence = 0;
    const view = applyEvents(emptyRun(), [
      event("state", job("AwaitingInput")),
      event("question", question("Pending")),
      event("state", job("Cancelled")),
    ]).view;

    expect(view.question).toBeNull();
  });

  test("unknown event types keep the sequence moving", () => {
    sequence = 0;
    const view = applyEvents(emptyRun(), [event("tool_call", { name: "future" }), event("state", job("Running"))]).view;

    expect(view.lastSequence).toBe(2);
    expect(view.job?.state).toBe("Running");
  });
});

test("severityCounts orders by severity and omits empty ones", () => {
  const findings = [finding("a", "Low"), finding("b", "Critical"), finding("c", "Low")] as never[];

  expect(severityCounts(findings)).toEqual([
    { severity: "Critical", count: 1 },
    { severity: "Low", count: 2 },
  ]);
});
