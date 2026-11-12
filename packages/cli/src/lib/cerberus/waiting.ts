import { useSyncExternalStore } from "react";
import type { Job, JobState } from "./api";

// Keeps track of the operator's runs while they are doing something else:
// which ones are waiting for them, and which have just ended. A run the
// operator is looking at needs no notice — its own screen shows it.

const WAITING: JobState[] = ["AwaitingApproval", "AwaitingInput"];
const TERMINAL: JobState[] = ["Succeeded", "Failed", "Cancelled", "TimedOut"];

const KIND: Record<string, string> = {
  scan: "Scan",
  assess: "Assessment",
  triage: "Triage",
  review: "Review",
  sast: "Code review",
  evidence: "Evidence run",
  "threat-model": "Threat model",
  investigate: "Team review",
  index: "Indexing",
};

export const describeJob = (job: Pick<Job, "type" | "targetPath">) => `${KIND[job.type] ?? job.type} of ${job.targetPath}`;

export type Notice = { message: string; variant: "info" | "error" };

/** What is known of each run seen so far: its last state. */
export type Seen = ReadonlyMap<string, JobState>;

export const waitingCount = (seen: Seen) => [...seen.values()].filter((state) => WAITING.includes(state)).length;

/**
 * Takes in one run's current state. Returns what is now known, and a notice
 * when the change is one the operator should hear about: a run started
 * waiting for them, or a run they had in flight ended. A run first seen
 * already finished is old news.
 */
export function noteJob(seen: Seen, job: Job): { seen: Seen; notice: Notice | null } {
  const before = seen.get(job.id);
  if (before === job.state) return { seen, notice: null };

  const next = new Map(seen).set(job.id, job.state);
  let notice: Notice | null = null;

  if (WAITING.includes(job.state)) {
    const what = job.state === "AwaitingApproval" ? "approval" : "answer";
    notice = { message: `${describeJob(job)} is waiting for your ${what} — open it with /runs`, variant: "info" };
  } else if (TERMINAL.includes(job.state) && before !== undefined) {
    notice =
      job.state === "Succeeded"
        ? { message: `${describeJob(job)} finished`, variant: "info" }
        : {
            message: `${describeJob(job)} ${job.state === "Failed" ? "failed" : job.state === "Cancelled" ? "was cancelled" : "timed out"}${job.error ? `: ${job.error}` : ""}`,
            variant: "error",
          };
  }
  return { seen: next, notice };
}

// --- the count, for the status bar ----------------------------------------

let count = 0;
const listeners = new Set<() => void>();

export function setWaitingCount(next: number) {
  if (next === count) return;
  count = next;
  for (const listener of listeners) listener();
}

const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
};

export const useWaitingCount = () => useSyncExternalStore(subscribe, () => count, () => count);

// --- the watcher ------------------------------------------------------------

export type WatcherDeps = {
  signedIn: () => boolean;
  listJobs: () => Promise<Job[]>;
  /** Subscribes to live job updates. `ensure` (re)connects if needed; it is called on every tick. */
  connect: (onJob: (job: Job) => void) => { ensure: () => Promise<void>; stop: () => void };
  notify: (notice: Notice) => void;
  /** Whether the operator is looking at that run right now. */
  isViewing: (jobId: string) => boolean;
  intervalMs?: number;
};

export type Watcher = { stop: () => void; refresh: () => Promise<void> };

/**
 * Watches the operator's runs in the background: live over the push channel,
 * with a periodic re-read as the safety net (and to pick up a sign-in).
 */
export function createWaitingWatcher(deps: WatcherDeps): Watcher {
  let seen: Seen = new Map();
  let loaded = false;
  let stopped = false;

  const take = (job: Job, quiet: boolean) => {
    const result = noteJob(seen, job);
    seen = result.seen;
    setWaitingCount(waitingCount(seen));
    if (result.notice && !quiet && !deps.isViewing(job.id)) deps.notify(result.notice);
  };

  const live = deps.connect((job) => {
    if (!stopped && loaded) take(job, false);
  });

  const refresh = async () => {
    if (stopped) return;
    if (!deps.signedIn()) {
      seen = new Map();
      loaded = false;
      setWaitingCount(0);
      return;
    }
    try {
      const jobs = await deps.listJobs();
      if (stopped) return;
      const first = !loaded;
      // The first read only takes stock: one summary instead of a notice per old run.
      for (const job of jobs) take(job, first);
      loaded = true;
      if (first) {
        const waiting = jobs.filter((job) => WAITING.includes(job.state) && !deps.isViewing(job.id)).length;
        if (waiting > 0) {
          deps.notify({ message: `${waiting} run${waiting === 1 ? " is" : "s are"} waiting for you — open with /runs`, variant: "info" });
        }
      }
      await live.ensure();
    } catch {
      // The server is unreachable or the sign-in lapsed: try again on the next tick.
    }
  };

  const timer = setInterval(() => void refresh(), deps.intervalMs ?? 15_000);
  void refresh();

  return {
    refresh,
    stop: () => {
      stopped = true;
      clearInterval(timer);
      live.stop();
      setWaitingCount(0);
    },
  };
}
