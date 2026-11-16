import { describe, expect, test } from "bun:test";
import type { Job } from "./api";
import { createWaitingWatcher, describeJob, noteJob, waitingCount, type Notice, type Seen } from "./waiting";

const job = (id: string, state: Job["state"], extra: Partial<Job> = {}): Job => ({
  id, type: "scan", state, tier: "T0", engagementId: null, targetPath: "svc", resultId: null, approvalId: null,
  error: null, createdAt: "2026-11-12T10:00:00Z", startedAt: null, finishedAt: null, ...extra,
});

/** Applies jobs in order and returns the notices they caused. */
function run(jobs: Job[], seen: Seen = new Map()) {
  const notices: string[] = [];
  for (const j of jobs) {
    const result = noteJob(seen, j);
    seen = result.seen;
    if (result.notice) notices.push(`${result.notice.variant}: ${result.notice.message}`);
  }
  return { seen, notices };
}

describe("noteJob", () => {
  test("a run that starts waiting is announced once, with what it waits for", () => {
    const { seen, notices } = run([
      job("a", "Running", { type: "review" }),
      job("a", "AwaitingApproval", { type: "review" }),
      job("a", "AwaitingApproval", { type: "review" }),
      job("b", "AwaitingInput"),
    ]);

    expect(notices).toEqual([
      "info: Review of svc is waiting for your approval — open it with /runs",
      "info: Scan of svc is waiting for your answer — open it with /runs",
    ]);
    expect(waitingCount(seen)).toBe(2);
  });

  test("a run that stops waiting no longer counts", () => {
    const { seen } = run([job("a", "AwaitingApproval"), job("a", "Queued"), job("a", "Running")]);
    expect(waitingCount(seen)).toBe(0);
  });

  test("a run that was in flight says how it ended; one first seen finished says nothing", () => {
    const { notices } = run([
      job("old", "Succeeded"),
      job("a", "Running", { type: "sast" }),
      job("a", "Succeeded", { type: "sast" }),
      job("b", "Queued"),
      job("b", "Failed", { error: "The target is not a directory." }),
      job("c", "Running", { type: "threat-model" }),
      job("c", "Cancelled", { type: "threat-model" }),
    ]);

    expect(notices).toEqual([
      "info: Code review of svc finished",
      "error: Scan of svc failed: The target is not a directory.",
      "error: Threat model of svc was cancelled",
    ]);
  });

  test("an unknown kind of job is named as it is", () => {
    expect(describeJob({ type: "fuzz", targetPath: "x" })).toBe("fuzz of x");
  });
});

describe("the watcher", () => {
  function setup(initial: Job[], options: { signedIn?: boolean; viewing?: string } = {}) {
    let jobs = initial;
    let push: (job: Job) => void = () => {};
    const notices: Notice[] = [];
    const calls = { ensure: 0, stopped: false };
    const watcher = createWaitingWatcher({
      signedIn: () => options.signedIn ?? true,
      listJobs: async () => jobs,
      connect: (onJob) => {
        push = onJob;
        return { ensure: async () => { calls.ensure++; }, stop: () => { calls.stopped = true; } };
      },
      notify: (notice) => notices.push(notice),
      isViewing: (id) => id === options.viewing,
      intervalMs: 60_000,
    });
    return { watcher, notices, calls, push: (j: Job) => push(j), setJobs: (next: Job[]) => { jobs = next; } };
  }

  test("on start it takes stock: one summary for runs already waiting, nothing for old ones", async () => {
    const w = setup([job("a", "AwaitingApproval"), job("b", "AwaitingInput"), job("c", "Succeeded"), job("d", "Running")]);
    await w.watcher.refresh();

    expect(w.notices.map((n) => n.message)).toEqual(["2 runs are waiting for you — open with /runs"]);
    expect(w.calls.ensure).toBeGreaterThan(0);
    w.watcher.stop();
    expect(w.calls.stopped).toBe(true);
  });

  test("a live update announces a run that starts waiting, and one that ends", async () => {
    const w = setup([job("d", "Running", { type: "review" })]);
    await w.watcher.refresh();

    w.push(job("d", "AwaitingInput", { type: "review" }));
    w.push(job("d", "Running", { type: "review" }));
    w.push(job("d", "Succeeded", { type: "review" }));

    expect(w.notices.map((n) => n.message)).toEqual([
      "Review of svc is waiting for your answer — open it with /runs",
      "Review of svc finished",
    ]);
    w.watcher.stop();
  });

  test("the run on screen is not announced; the periodic re-read catches what the push channel missed", async () => {
    const w = setup([job("a", "Running"), job("b", "Running")], { viewing: "a" });
    await w.watcher.refresh();

    w.push(job("a", "AwaitingApproval"));                       // being looked at: no notice
    w.setJobs([job("a", "AwaitingApproval"), job("b", "TimedOut")]);
    await w.watcher.refresh();                                   // b ended without a push

    expect(w.notices.map((n) => `${n.variant}: ${n.message}`)).toEqual(["error: Scan of svc timed out"]);
    w.watcher.stop();
  });

  test("signed out, it asks the server nothing and announces nothing", async () => {
    const w = setup([job("a", "AwaitingApproval")], { signedIn: false });
    await w.watcher.refresh();
    w.push(job("a", "AwaitingApproval"));

    expect(w.notices).toEqual([]);
    expect(w.calls.ensure).toBe(0);
    w.watcher.stop();
  });
});
