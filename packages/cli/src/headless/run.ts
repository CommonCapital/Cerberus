import type { Approval, Job, SubmitJob } from "../lib/cerberus/api";
import { sarifSourceOf, type SarifSource } from "../lib/cerberus/export";
import type { Follower } from "../lib/cerberus/follow";
import { isFinished, type RunView } from "../lib/cerberus/run-state";
import { summarizeToolCall } from "../lib/tool-display";
import { SEVERITIES, type RunOptions } from "./args";
import { failing, readSarif, summarize, type GateFinding, type Summary } from "./gate";

// One job from start to exit code, with nobody at the keyboard. The server
// does the work; this follows the run, answers only what was approved in
// advance on the command line, and turns the outcome into a result a pipeline
// can read. Everything that touches the network, the disk or the clock is
// passed in (see main.ts), so the whole flow is tested without a server.

export const EXIT = { ok: 0, findings: 1, notFinished: 2, needsPerson: 3, usage: 64 } as const;

export type HeadlessDeps = {
  submit: (job: SubmitJob) => Promise<Job>;
  follow: (jobId: string, onChange: (view: RunView) => void, onError?: (error: Error) => void) => Follower;
  approve: (approvalId: string, note: string) => Promise<unknown>;
  cancel: (jobId: string) => Promise<unknown>;
  sarif: (source: SarifSource) => Promise<unknown>;
  /** The written report of a finished job (Markdown), or null if it has none. */
  report: (job: Job) => Promise<string | null>;
  /** Writes a file and returns the path it was written to. */
  write: (path: string, content: string) => string;
  /** The screenshots a run kept, with their bytes. */
  screenshots: (jobId: string) => Promise<{ name: string; source: string; bytes: Uint8Array }[]>;
  writeBytes: (path: string, bytes: Uint8Array) => string;
  /** The result: stdout. */
  out: (line: string) => void;
  /** Progress and problems: stderr. */
  log: (line: string) => void;
  /** Calls back after a while; returns how to call that off. */
  after: (ms: number, callback: () => void) => () => void;
  now: () => number;
};

const LABEL: Record<string, string> = {
  scan: "Scan",
  review: "Review",
  sast: "SAST agent run",
  "threat-model": "Threat model",
  index: "Code index",
};

/** The same, in the middle of a sentence. */
const lower = (label: string) => (label.startsWith("SAST") ? label : label.toLowerCase());

/** A page title as part of a file name: letters, digits and dashes, not too long. */
const slug = (name: string) => name.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 50) || "page";

const short = (id: string) => id.slice(0, 8);
const message = (problem: unknown) => (problem instanceof Error ? problem.message : String(problem));
const plural = (count: number, word: string) => `${count} ${word}${count === 1 ? "" : "s"}`;

/** Every kind of action an approval request covers. */
const kindsOf = (approval: Approval) => (approval.kinds && approval.kinds.length > 0 ? approval.kinds : [approval.kind]);

type Outcome =
  | { kind: "finished"; view: RunView }
  | { kind: "needs-person"; reason: string }
  | { kind: "timeout" }
  | { kind: "lost"; reason: string };

/** A push channel hiccup is not the end of a run; the server staying away is. */
const LOST_AFTER_ERRORS = 5;
const LOST_WITHIN_MS = 60_000;

/** Prints what is new in the run since the last time: steps, tool calls, notes, decisions. */
function progressPrinter(say: (line: string) => void) {
  const steps = new Map<string, string>();
  let tools = 0;
  let notes = 0;
  let resolved = 0;
  let state = "";

  return (view: RunView) => {
    if (view.job && view.job.state !== state) {
      state = view.job.state;
      if (state === "AwaitingApproval") say("[wait] needs approval before it starts");
      if (state === "AwaitingInput") say("[wait] has a question");
    }
    for (const step of view.steps) {
      if (steps.get(step.id) === step.status) continue;
      steps.set(step.id, step.status);
      if (step.status === "running") say(`[step] ${step.title}`);
      if (step.status === "completed") say(`[done] ${step.title}`);
      if (step.status === "skipped") say(`[skip] ${step.title}`);
    }
    for (const tool of view.tools.slice(tools)) say(`[tool] ${tool.name} ${summarizeToolCall(tool.name, tool.input)}`.trimEnd());
    tools = view.tools.length;
    for (const note of view.notes.slice(notes)) say(`[note] ${note}`);
    notes = view.notes.length;
    for (const line of view.resolved.slice(resolved)) say(`[decided] ${line}`);
    resolved = view.resolved.length;
  };
}

function watch(jobId: string, options: RunOptions, deps: HeadlessDeps, say: (line: string) => void): Promise<Outcome> {
  return new Promise((resolve) => {
    const print = progressPrinter(say);
    const handled = new Set<string>();
    const errors: number[] = [];
    let settled = false;
    let follower: Follower | null = null;

    const finish = (outcome: Outcome) => {
      if (settled) return;
      settled = true;
      callOffTimeout();
      void (follower?.stop() ?? Promise.resolve()).catch(() => {}).then(() => resolve(outcome));
    };
    const callOffTimeout = deps.after(options.timeoutMinutes * 60_000, () => finish({ kind: "timeout" }));

    follower = deps.follow(
      jobId,
      (view) => {
        if (settled) return;
        print(view);

        const approval = view.approval;
        if (approval && !handled.has(approval.id)) {
          handled.add(approval.id);
          const missing = kindsOf(approval).filter((kind) => !options.approve.includes(kind));
          if (missing.length > 0) {
            finish({
              kind: "needs-person",
              reason:
                `It needs approval: ${approval.title}. ${approval.detail}\n` +
                `To approve this in advance, add --approve ${kindsOf(approval).join(",")}; or allow it for good in the CLI with /permissions.`,
            });
            return;
          }
          say(`[approved] ${approval.title} (in advance: --approve ${kindsOf(approval).join(",")})`);
          deps
            .approve(approval.id, "Approved in advance on the command line of a headless run (--approve).")
            .catch((problem) => finish({ kind: "lost", reason: `The approval could not be given: ${message(problem)}` }));
          return;
        }

        if (view.question) {
          finish({ kind: "needs-person", reason: `It asks a question nobody is here to answer: ${view.question.text}` });
          return;
        }
        if (isFinished(view)) finish({ kind: "finished", view });
      },
      (problem) => {
        if (settled) return;
        const now = deps.now();
        errors.push(now);
        while (errors.length > 0 && now - errors[0]! > LOST_WITHIN_MS) errors.shift();
        if (errors.length >= LOST_AFTER_ERRORS) finish({ kind: "lost", reason: message(problem) });
      },
    );
    if (settled) void follower.stop().catch(() => {});
  });
}

function describeCounts(summary: Summary): string {
  const parts = SEVERITIES.filter((s) => summary.bySeverity[s] > 0).map((s) => `${summary.bySeverity[s]} ${s}`);
  return parts.length > 0 ? ` (${parts.join(", ")})` : "";
}

const MAX_LISTED = 20;

const listed = (finding: GateFinding) =>
  `  ${finding.severity.padEnd(8)}  ${finding.file}${finding.line > 0 ? `:${finding.line}` : ""}  ${finding.title}${finding.fromModel ? "  [model]" : ""}`;

/** Runs the job and returns the process's exit code. */
export async function runHeadless(options: RunOptions, deps: HeadlessDeps): Promise<number> {
  const say = options.quiet ? () => {} : deps.log;
  const label = LABEL[options.job.type] ?? options.job.type;

  let job: Job;
  try {
    job = await deps.submit(options.job);
  } catch (problem) {
    deps.log(`Could not start the ${lower(label)}: ${message(problem)}`);
    return EXIT.notFinished;
  }
  say(`[start] ${label} ${short(job.id)} on ${job.targetPath || "."}`);

  const outcome = await watch(job.id, options, deps, say);

  if (outcome.kind !== "finished") {
    // Nobody will come back to it: do not leave it waiting or running on the server.
    await deps.cancel(job.id).catch(() => {});
    const why =
      outcome.kind === "timeout"
        ? `It did not finish within ${options.timeoutMinutes} minute${options.timeoutMinutes === 1 ? "" : "s"} (--timeout).`
        : outcome.kind === "lost"
          ? `The server stopped answering: ${outcome.reason}`
          : outcome.reason;
    deps.log(`${label} ${short(job.id)} was stopped. ${why}`);
    const exitCode = outcome.kind === "needs-person" ? EXIT.needsPerson : EXIT.notFinished;
    if (options.json) deps.out(JSON.stringify({ job: { id: job.id, type: job.type, state: "Cancelled" }, stopped: outcome.kind, reason: why, exitCode }));
    return exitCode;
  }

  job = outcome.view.job ?? job;
  if (job.state !== "Succeeded") {
    const how = job.state === "TimedOut" ? "ran out of time on the server" : job.state.toLowerCase();
    deps.log(`${label} ${short(job.id)} ${how}${job.error ? `: ${job.error}` : "."}`);
    if (options.json) deps.out(JSON.stringify({ job: { id: job.id, type: job.type, state: job.state, error: job.error }, exitCode: EXIT.notFinished }));
    return EXIT.notFinished;
  }

  const files: { sarif?: string; report?: string; screenshots?: string[] } = {};
  let summary: Summary | null = null;
  try {
    const source = sarifSourceOf(job);
    if (source) {
      const log = await deps.sarif(source);
      summary = summarize(readSarif(log), { includeTests: options.includeTests });
      if (options.sarif) files.sarif = deps.write(options.sarif, `${JSON.stringify(log, null, 2)}\n`);
    }
    if (options.screenshots) {
      const shots = await deps.screenshots(job.id);
      files.screenshots = shots.map((shot, index) =>
        deps.writeBytes(`${options.screenshots}/${String(index + 1).padStart(2, "0")}-${slug(shot.name)}.png`, shot.bytes),
      );
      for (const [index, shot] of shots.entries()) say(`[screenshot] ${files.screenshots[index]} — ${shot.source}`);
    }
    if (options.report) {
      const report = await deps.report(job);
      if (report) files.report = deps.write(options.report, report.endsWith("\n") ? report : `${report}\n`);
      else deps.log(`This ${lower(label)} wrote no report, so ${options.report} was not written.`);
    }
  } catch (problem) {
    // The job is done, but without its result there is nothing to pass a build on.
    deps.log(`${label} ${short(job.id)} finished, but its result could not be collected: ${message(problem)}`);
    return EXIT.notFinished;
  }

  const blocking = summary ? failing(summary, options.failOn) : [];
  const exitCode = blocking.length > 0 ? EXIT.findings : EXIT.ok;

  if (options.json) {
    deps.out(
      JSON.stringify({
        job: { id: job.id, type: job.type, state: job.state, targetPath: job.targetPath, resultId: job.resultId },
        findings: summary && {
          counted: summary.counted.length,
          bySeverity: summary.bySeverity,
          fromModel: summary.fromModel,
          notCounted: { ignoredInCode: summary.ignoredInCode, decided: summary.decided, inTestsOrDocs: summary.inTestsOrDocs },
        },
        gate: summary && { failOn: options.failOn, failed: blocking.length > 0, findings: blocking.length },
        files,
        exitCode,
      }),
    );
    return exitCode;
  }

  if (!summary) {
    deps.out(`${label} ${short(job.id)} finished.`);
  } else {
    deps.out(`${label} ${short(job.id)} finished: ${plural(summary.counted.length, "finding")} counted${describeCounts(summary)}.`);
    const left = [
      summary.ignoredInCode > 0 ? `${summary.ignoredInCode} marked to be ignored in the code` : "",
      summary.decided > 0 ? `${summary.decided} decided by an operator` : "",
      summary.inTestsOrDocs > 0 ? `${summary.inTestsOrDocs} in tests or documentation` : "",
    ].filter(Boolean);
    if (left.length > 0) deps.out(`Not counted: ${left.join(", ")}.`);
    if (summary.fromModel > 0) {
      deps.out(`${summary.fromModel} of the counted ${summary.fromModel === 1 ? "is a model's candidate" : "are a model's candidates"}, for a person to check.`);
    }
  }
  if (files.sarif) deps.out(`SARIF written to ${files.sarif}`);
  if (files.report) deps.out(`Report written to ${files.report}`);
  if (files.screenshots) {
    deps.out(files.screenshots.length > 0
      ? `${plural(files.screenshots.length, "screenshot")} saved to ${options.screenshots}`
      : "The run kept no screenshots.");
  }

  if (summary) {
    if (options.failOn === "none") {
      deps.out("No gate (--fail-on none).");
    } else if (blocking.length === 0) {
      deps.out(`PASSED: nothing at or above ${options.failOn}.`);
    } else {
      deps.out(`Findings at or above ${options.failOn}:`);
      for (const finding of blocking.slice(0, MAX_LISTED)) deps.out(listed(finding));
      if (blocking.length > MAX_LISTED) deps.out(`  and ${blocking.length - MAX_LISTED} more`);
      deps.out(`FAILED: ${plural(blocking.length, "finding")} at or above ${options.failOn}.`);
    }
  }
  return exitCode;
}
