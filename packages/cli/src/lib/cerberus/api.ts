import { clearAuth, getAuth } from "../auth";
import type { ModelListing } from "../model-choices";
import { explainSubmitError, getCurrentEngagement, setCurrentEngagement, underEngagement } from "./engagement";

// Client for the Cerberus (.NET) backend's job, approval and question
// endpoints. It talks to the same server address as the rest of the CLI
// (API_URL) with the same session token, using plain fetch: these endpoints
// are not part of the TypeScript server's typed (hono) client.

export const baseUrl = () => (process.env.API_URL ?? "http://localhost:3000").replace(/\/+$/, "");

export type JobState =
  | "AwaitingApproval"
  | "Queued"
  | "Running"
  | "AwaitingInput"
  | "Succeeded"
  | "Failed"
  | "Cancelled"
  | "TimedOut";

export type Job = {
  id: string;
  type: string;
  state: JobState;
  tier: string;
  engagementId: string | null;
  targetPath: string;
  resultId: string | null;
  approvalId: string | null;
  error: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
};

export type Approval = {
  id: string;
  jobId: string;
  kind: string;
  /** Every kind of action the request covers. */
  kinds?: string[];
  title: string;
  detail: string;
  status: "Pending" | "Approved" | "Denied" | "Cancelled";
  note: string | null;
};

export type QuestionOption = { label: string; description?: string | null };

export type Question = {
  id: string;
  jobId: string;
  text: string;
  options: QuestionOption[];
  multiSelect: boolean;
  allowText: boolean;
  status: "Pending" | "Answered" | "Expired" | "Cancelled";
  answer: { selected: string[]; text: string | null } | null;
};

export type RunEvent = {
  jobId: string;
  sequence: number;
  type: string;
  data: unknown;
  createdAt: string;
};

export type EventsPage = { jobId: string; events: RunEvent[]; lastSequence: number; more: boolean };

export type Permissions = {
  mode: "Default" | "Ask";
  allowedKinds: string[];
  /** Command rules: shell command prefixes ("git status") the chat agent may run without asking. */
  allowedCommands?: string[];
  scope: "standard" | "user" | "engagement";
  kinds: { kind: string; description: string; asks: boolean }[];
};

export type EngagementTarget = {
  id: string;
  type: "Path" | "Host" | "Domain";
  locator: string;
  verification: string;
};

export type Engagement = {
  id: string;
  name: string;
  status: "Active" | "Closed" | "Aborted";
  maxTier: string;
  targets: EngagementTarget[];
  createdAt: string;
};

export type FindingDecision = {
  id: string;
  status: "accepted-risk" | "false-positive";
  note: string;
  decidedAt: string;
};

export type ScanFinding = {
  id: string;
  title: string;
  severity: "Info" | "Low" | "Medium" | "High" | "Critical";
  category: string;
  file: string;
  line: number;
  /** The code on that line, with credentials masked. */
  evidence: string;
  remediation?: string | null;
  /** "test" or "docs" when the file is not production code. */
  context?: string | null;
  /** The target itself marked this line to be ignored. */
  suppressed?: boolean;
  /** The operator's decision on this finding; it follows the finding across scans. */
  decision: FindingDecision | null;
};

export type ScanSummary = { id: string; targetPath: string; findingCount: number; createdAt: string };

export type ScanDetail = ScanSummary & { findings: ScanFinding[] };

export type AssessmentSummary = {
  id: string;
  catalogId: string;
  targetPath: string;
  totalControls: number;
  compliant: number;
  nonCompliant: number;
  notAssessed: number;
  score: number | null;
  createdAt: string;
};

export type ControlAnswer = { verdict: string; justification: string; actionPlan: string | null; timeLimitDays: number | null };

export type SuggestedAnswer = {
  controlId: string;
  suggestion: string;
  reasoning: string;
  evidence: { file: string; line: number; text: string }[];
  model: string;
};

export type AssessmentControl = {
  controlId: string;
  section: string;
  title: string;
  /** What the scanner established on its own. */
  verdict: "Compliant" | "NonCompliant" | "NotAssessed";
  rationale: string;
  /** The operator's answer; it overrules the scanner's verdict. */
  response: ControlAnswer | null;
  /** A model's suggested answer. Changes nothing until accepted. */
  suggestion: SuggestedAnswer | null;
};

export type Assessment = {
  id: string;
  catalogId: string;
  targetPath: string;
  createdAt: string;
  summary: { totalControls: number; compliant: number; nonCompliant: number; notAssessed: number; score: number | null };
  effective: {
    compliant: number;
    partiallyCompliant: number;
    nonCompliant: number;
    notApplicable: number;
    unanswered: number;
    operatorAnswered: number;
  };
  controls: AssessmentControl[];
};

export type MemoryEntry = {
  id: string;
  kind: "instruction" | "note";
  /** Who wrote it. An agent's note is a hint for later runs, not an instruction. */
  source: "operator" | "agent";
  text: string;
  engagementId: string | null;
  targetPath: string | null;
  jobId: string | null;
  createdAt: string;
};

export type ScheduleInfo = {
  id: string;
  name: string;
  type: string;
  targetPath: string;
  everyMinutes: number;
  /** "every day", "every 6 hours" */
  every: string;
  enabled: boolean;
  nextRunAt: string;
  lastRunAt: string | null;
  lastOutcome: string | null;
  lastJobId: string | null;
};

export type Catalog = { id: string; name: string; version: string; controlCount: number };

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const auth = getAuth();
  let response: Response;
  try {
    response = await fetch(baseUrl() + path, {
      method,
      headers: {
        ...(auth ? { Authorization: `Bearer ${auth.token}` } : {}),
        ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new ApiError(`Cannot reach the server at ${baseUrl()}. Is the backend running?`, 0);
  }

  if (response.status === 401) {
    clearAuth();
    setCurrentEngagement(null);   // it belonged to that sign-in
    throw new ApiError("Not signed in. Run /login first.", 401);
  }

  const text = await response.text();
  let data: unknown = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    // Not JSON: handled below.
  }

  if (!response.ok) {
    const message =
      data && typeof data === "object" && typeof (data as { error?: unknown }).error === "string"
        ? (data as { error: string }).error
        : response.status === 404
          ? "This server does not offer that feature. Scans and jobs need the Cerberus (.NET) backend."
          : `Request failed (${response.status})`;
    throw new ApiError(message, response.status);
  }
  return data as T;
}

/** A response body that is not JSON: a file the server builds (a transcript). */
async function download(path: string): Promise<string> {
  const auth = getAuth();
  let response: Response;
  try {
    response = await fetch(baseUrl() + path, { headers: auth ? { Authorization: `Bearer ${auth.token}` } : {} });
  } catch {
    throw new ApiError(`Cannot reach the server at ${baseUrl()}. Is the backend running?`, 0);
  }
  const text = await response.text();
  if (!response.ok) {
    let message = `Request failed (${response.status})`;
    try {
      const data = JSON.parse(text) as { error?: unknown };
      if (typeof data.error === "string") message = data.error;
    } catch {
      // Not JSON: the status says enough.
    }
    throw new ApiError(message, response.status);
  }
  return text;
}

export type SubmitJob =
  | { type: "scan"; path: string; sca?: boolean; exclusions?: "ask" | "honour" | "ignore"; engagementId?: string }
  | { type: "assess"; catalogId: string; path?: string; scanId?: string; sca?: boolean; engagementId?: string }
  | { type: "triage"; scanId: string; model: string; maxFindings?: number }
  /** The SAST agent: a model reads the code under `path` and reports vulnerabilities. */
  | { type: "sast"; path: string; model: string; maxSteps?: number; instructions?: string; engagementId?: string; research?: boolean }
  /** Builds or refreshes the semantic index of the code under `path`, with the server's local embedding model. */
  | { type: "index"; path: string; engagementId?: string }
  /** The threat-model agent: a model maps the components and threats of the code under `path` and writes them up. */
  | { type: "threat-model"; path: string; model: string; maxSteps?: number; instructions?: string; engagementId?: string; research?: boolean }
  /** The evidence agent: a model suggests answers for controls of an assessment the scanner could not judge. */
  | { type: "evidence"; assessmentId: string; model: string; maxControls?: number; maxSteps?: number; controlIds?: string[] }
  /** A planned review: scan, optional model triage, one assessment per catalog (all if none named), a report. */
  | {
      type: "review";
      path: string;
      catalogs?: string[];
      model?: string;
      /** The model that runs the review's agents. Without `agents`, only the SAST agent runs. */
      agentModel?: string;
      /** Which agents the review dispatches with `agentModel`. */
      agents?: ("sast" | "threat-model" | "evidence")[];
      sca?: boolean;
      exclusions?: "ask" | "honour" | "ignore";
      engagementId?: string;
    };

export type Review = {
  id: string;
  jobId: string;
  targetPath: string;
  scanId: string;
  assessmentIds: string[];
  findingCount: number;
  createdAt: string;
  /** Markdown. */
  report: string | null;
};

export const cerberus = {
  /**
   * Starts a job — under the engagement the operator has chosen, unless the job
   * names one itself. A job that needs approval comes back as `{ job, approval }`;
   * this unwraps it to the job.
   */
  async submitJob(job: SubmitJob): Promise<Job> {
    try {
      const result = await request<Job | { job: Job }>("POST", "/jobs", underEngagement(job, getCurrentEngagement()));
      return "job" in result ? result.job : result;
    } catch (problem) {
      if (problem instanceof ApiError) throw new ApiError(explainSubmitError(problem.message), problem.status);
      throw problem;
    }
  },
  engagements: () => request<Engagement[]>("GET", "/engagements"),
  engagement: (id: string) => request<Engagement>("GET", `/engagements/${id}`),
  createEngagement: (name: string) => request<Engagement>("POST", "/engagements", { name }),
  /** Adds a directory to the engagement's scope. Calling this is the operator's attestation that they may test it. */
  addPathTarget: (engagementId: string, path: string) =>
    request<EngagementTarget>("POST", `/engagements/${engagementId}/targets`, { type: "path", locator: path, ownershipAttested: true }),
  removeTarget: (engagementId: string, targetId: string) =>
    request<null>("DELETE", `/engagements/${engagementId}/targets/${targetId}`),
  /** The kill switch: stops the engagement's queued and running jobs and refuses further ones. */
  abortEngagement: (id: string) => request<unknown>("POST", `/engagements/${id}/abort`),
  job: (id: string) => request<Job>("GET", `/jobs/${id}`),
  jobs: (limit = 30) => request<Job[]>("GET", `/jobs?limit=${limit}`),
  /** Says something to the running agent of a job; it reads it before its next step. */
  sendMessage: (jobId: string, text: string) => request<unknown>("POST", `/jobs/${jobId}/messages`, { text }),
  cancelJob: (id: string) => request<unknown>("POST", `/jobs/${id}/cancel`),
  events: (jobId: string, after = 0) => request<EventsPage>("GET", `/jobs/${jobId}/events?after=${after}`),
  /** `remember` also stops the server asking about this kind of action again. */
  approve: (id: string, options: { note?: string; remember?: boolean } = {}) =>
    request<Approval>("POST", `/approvals/${id}/approve`, options),
  permissions: () => request<Permissions>("GET", "/settings/permissions"),
  setPermissions: (change: { mode?: "default" | "ask"; allowedKinds?: string[]; allowedCommands?: string[] }) =>
    request<Permissions>("PUT", "/settings/permissions", change),
  deny: (id: string, note?: string) => request<Approval>("POST", `/approvals/${id}/deny`, { note }),
  answer: (id: string, selected: string[], text?: string) =>
    request<Question>("POST", `/questions/${id}/answer`, { selected, text }),
  review: (id: string) => request<Review>("GET", `/reviews/${id}`),
  /** What an agent job produced; `report` is its written report (Markdown) when the agent writes one. */
  agentRun: (id: string) => request<{ id: string; agent: string; summary: string; report: string | null }>("GET", `/agent-runs/${id}`),
  /** Summarises the earlier part of a chat now; the model gets the summary in its place from the next reply on. */
  compactSession: (sessionId: string, model?: string) =>
    request<{ messages: number; summary: string; model: string }>("POST", `/sessions/${sessionId}/compact`, model ? { model } : {}),
  /** Has the model of the last reply name the conversation from its first prompt. */
  nameSession: (sessionId: string) => request<{ title: string; model: string }>("POST", `/sessions/${sessionId}/title`),
  /** Names the conversation as the operator says. */
  renameSession: (sessionId: string, title: string) => request<{ title: string }>("PUT", `/sessions/${sessionId}/title`, { title }),
  /** A conversation with its messages. */
  session: (sessionId: string) => request<{ id: string; title: string; messages: unknown[] }>("GET", `/sessions/${sessionId}`),
  /** A copy of the conversation — whole, or up to the reply `through` — to carry on differently. */
  forkSession: (sessionId: string, options: { through?: string; title?: string } = {}) =>
    request<{ id: string; title: string }>("POST", `/sessions/${sessionId}/fork`, options),
  /** The conversation as a Markdown document, credentials masked. */
  exportSession: (sessionId: string) => download(`/sessions/${sessionId}/export`),
  /** What a run kept besides its findings: the screenshots its researcher took. */
  artifacts: (jobId: string) =>
    request<{ id: string; name: string; contentType: string; source: string; size: number; createdAt: string }[]>("GET", `/jobs/${jobId}/artifacts`),
  artifactBytes: async (id: string): Promise<Uint8Array> => {
    const auth = getAuth();
    const response = await fetch(`${baseUrl()}/artifacts/${id}`, { headers: auth ? { Authorization: `Bearer ${auth.token}` } : {} });
    if (!response.ok) throw new ApiError(`The screenshot could not be fetched (${response.status})`, response.status);
    return new Uint8Array(await response.arrayBuffer());
  },
  /** What can be chosen as a model: every provider, its models, and whether it is ready to use. */
  models: () => request<ModelListing>("GET", "/models"),
  /** Which API providers have a key stored for this operator. A key itself is never returned. */
  apiKeys: () => request<{ provider: string; hasKey: boolean }[]>("GET", "/settings/keys"),
  setApiKey: (provider: string, apiKey: string) => request<unknown>("PUT", "/settings/keys", { provider, apiKey }),
  removeApiKey: (provider: string) => request<unknown>("DELETE", `/settings/keys/${provider}`),
  /** Recurring runs: each is a job request and how often the server submits it. */
  schedules: () => request<ScheduleInfo[]>("GET", "/schedules"),
  createSchedule: (schedule: { name: string | null; every: string; job: SubmitJob }) => request<ScheduleInfo>("POST", "/schedules", schedule),
  updateSchedule: (id: string, change: { enabled?: boolean; every?: string; name?: string }) => request<ScheduleInfo>("PUT", `/schedules/${id}`, change),
  deleteSchedule: (id: string) => request<null>("DELETE", `/schedules/${id}`),
  runSchedule: (id: string) => request<{ outcome: string; detail: string; jobId: string | null }>("POST", `/schedules/${id}/run`),
  scans: () => request<ScanSummary[]>("GET", "/scans"),
  scan: (id: string) => request<ScanDetail>("GET", `/scans/${id}`),
  /** Records the operator's decision on a finding, with the reason. It applies to that finding in later scans too. */
  decideFinding: (scanId: string, findingId: string, status: FindingDecision["status"], note: string) =>
    request<FindingDecision>("PUT", `/scans/${scanId}/findings/decision`, { findingId, status, note }),
  withdrawDecision: (decisionId: string) => request<null>("DELETE", `/decisions/${decisionId}`),
  /** The findings of a scan, a review or a SAST agent run as a SARIF 2.1.0 log. */
  sarif: (kind: "scans" | "reviews" | "agent-runs", id: string) => request<unknown>("GET", `/${kind}/${id}/sarif`),
  assessments: () => request<AssessmentSummary[]>("GET", "/compliance"),
  assessment: (id: string) => request<Assessment>("GET", `/compliance/${id}`),
  /** Makes a model's suggestion the operator's answer. A non-compliant one needs an action plan. */
  acceptSuggestion: (id: string, controlId: string, extra: { actionPlan?: string; timeLimitDays?: number } = {}) =>
    request<unknown>("POST", `/compliance/${id}/suggestions/${controlId}/accept`, extra),
  dismissSuggestion: (id: string, controlId: string) =>
    request<null>("DELETE", `/compliance/${id}/suggestions/${controlId}`),
  memory: () => request<MemoryEntry[]>("GET", "/memory"),
  remember: (kind: "instruction" | "note", text: string, targetPath?: string) =>
    request<MemoryEntry>("POST", "/memory", { kind, text, targetPath }),
  forget: (id: string) => request<null>("DELETE", `/memory/${id}`),
  catalogs: () => request<Catalog[]>("GET", "/compliance/catalogs"),
};
