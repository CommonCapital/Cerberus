import type { Approval, Job, JobState, Question, RunEvent } from "./api";

// Turns a run's event stream into what the screen shows. Pure and
// order-tolerant: events are applied by sequence number, duplicates are
// ignored, and a gap is reported so the caller can fetch what it missed.

export type Step = { id: string; title: string; status: "pending" | "running" | "completed" | "skipped" };

export type Finding = {
  id: string;
  title: string;
  severity: "Info" | "Low" | "Medium" | "High" | "Critical";
  category: string;
  file: string;
  line: number;
  context?: string | null;
};

export type TriageOpinion = {
  findingId: string;
  verdict: "TruePositive" | "FalsePositive" | "NeedsReview";
  reasoning: string;
  model: string;
};

/** One tool call of an agent, with its result once it has answered. */
export type ToolActivity = {
  id: string;
  name: string;
  input: unknown;
  /** Undefined while the tool is still running. */
  output?: string;
  truncated?: boolean;
  isError?: boolean;
};

export type RunView = {
  job: Job | null;
  lastSequence: number;
  steps: Step[];
  progress: { done: number; total: number; message: string | null } | null;
  findings: Finding[];
  /** Model output per channel, in arrival order of the channels. */
  text: { channel: string; text: string }[];
  triage: TriageOpinion[];
  /** What the agent did, in order. */
  tools: ToolActivity[];
  notes: string[];
  /** Set while the run waits for the operator's decision. */
  approval: Approval | null;
  /** Set while the run waits for the operator's answer. */
  question: Question | null;
  /** What the operator already decided or answered, for the record on screen. */
  resolved: string[];
};

export const emptyRun = (): RunView => ({
  job: null,
  lastSequence: 0,
  steps: [],
  progress: null,
  findings: [],
  text: [],
  triage: [],
  tools: [],
  notes: [],
  approval: null,
  question: null,
  resolved: [],
});

const TERMINAL: JobState[] = ["Succeeded", "Failed", "Cancelled", "TimedOut"];

export const isFinished = (view: RunView) => view.job !== null && TERMINAL.includes(view.job.state);

/** Whether the run has an agent that could be told something right now. */
export const canBeSteered = (view: RunView) =>
  view.job !== null &&
  view.job.state === "Running" &&
  (view.job.type === "sast" || view.tools.length > 0);

export const isWaitingForOperator = (view: RunView) => view.approval !== null || view.question !== null;

export type Applied = { view: RunView; gap: boolean };

/**
 * Applies events to a view. Events at or below the view's last sequence are
 * dropped (already seen). If the next event is not the direct successor, the
 * result reports a gap and stops there: the caller should fetch from
 * `view.lastSequence` and apply again.
 */
export function applyEvents(view: RunView, events: RunEvent[]): Applied {
  const ordered = [...events].sort((a, b) => a.sequence - b.sequence);
  let next = view;

  for (const event of ordered) {
    if (event.sequence <= next.lastSequence) continue;
    if (event.sequence !== next.lastSequence + 1) return { view: next, gap: true };
    next = applyOne(next, event);
  }
  return { view: next, gap: false };
}

function applyOne(view: RunView, event: RunEvent): RunView {
  const next: RunView = { ...view, lastSequence: event.sequence };
  const data = event.data as Record<string, unknown>;

  switch (event.type) {
    case "state": {
      next.job = data as unknown as Job;
      // A run that is no longer waiting has nothing left to ask.
      if (next.job.state !== "AwaitingApproval") next.approval = null;
      if (next.job.state !== "AwaitingInput") next.question = null;
      break;
    }
    case "step": {
      const step = data as unknown as Step;
      const index = view.steps.findIndex((s) => s.id === step.id);
      next.steps = index === -1 ? [...view.steps, step] : view.steps.map((s, i) => (i === index ? step : s));
      // Progress belongs to the step that reported it; a new step starts clean.
      if (step.status === "running") next.progress = null;
      break;
    }
    case "progress":
      next.progress = data as RunView["progress"];
      break;
    case "finding":
      next.findings = [...view.findings, data as unknown as Finding];
      break;
    case "text": {
      const { channel, text } = data as { channel: string; text: string };
      const index = view.text.findIndex((t) => t.channel === channel);
      next.text =
        index === -1
          ? [...view.text, { channel, text }]
          : view.text.map((t, i) => (i === index ? { channel, text: t.text + text } : t));
      break;
    }
    case "triage":
      next.triage = [...view.triage, data as unknown as TriageOpinion];
      break;
    case "tool_call":
      next.tools = [...view.tools, data as unknown as ToolActivity];
      break;
    case "tool_result": {
      const result = data as unknown as ToolActivity;
      next.tools = view.tools.map((tool) => (tool.id === result.id ? { ...tool, ...result, input: tool.input } : tool));
      break;
    }
    case "operator_message":
      next.resolved = [...view.resolved, `You told the agent: ${String(data.text ?? "")}`];
      break;
    case "note":
    case "truncated":
      next.notes = [...view.notes, String(data.message ?? "")];
      break;
    case "approval_requested":
      next.approval = data as unknown as Approval;
      break;
    case "approval_resolved": {
      const approval = data as unknown as Approval;
      next.approval = null;
      next.resolved = [
        ...view.resolved,
        `${approval.status}: ${approval.title}${approval.note ? ` — ${approval.note}` : ""}`,
      ];
      break;
    }
    case "question":
      next.question = data as unknown as Question;
      break;
    case "question_resolved": {
      const question = data as unknown as Question;
      next.question = null;
      const answer = question.answer
        ? [...question.answer.selected, ...(question.answer.text ? [question.answer.text] : [])].join(", ")
        : question.status.toLowerCase();
      next.resolved = [...view.resolved, `${question.text} → ${answer}`];
      break;
    }
    default:
      // An event type this client does not know yet: keep the sequence moving, show nothing.
      break;
  }
  return next;
}

const SEVERITY_ORDER = ["Critical", "High", "Medium", "Low", "Info"] as const;

/** Counts per severity, most severe first, omitting empty ones. */
export function severityCounts(findings: Finding[]): { severity: Finding["severity"]; count: number }[] {
  return SEVERITY_ORDER.map((severity) => ({
    severity,
    count: findings.filter((f) => f.severity === severity).length,
  })).filter((entry) => entry.count > 0);
}
