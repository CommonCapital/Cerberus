import { clearAuth, getAuth } from "../auth";

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
  scope: "standard" | "user" | "engagement";
  kinds: { kind: string; description: string; asks: boolean }[];
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

export type SubmitJob =
  | { type: "scan"; path: string; sca?: boolean; exclusions?: "ask" | "honour" | "ignore"; engagementId?: string }
  | { type: "assess"; catalogId: string; path?: string; scanId?: string; sca?: boolean; engagementId?: string }
  | { type: "triage"; scanId: string; model: string; maxFindings?: number };

export const cerberus = {
  /** A job that needs approval comes back as `{ job, approval }`; unwrap it to the job. */
  async submitJob(job: SubmitJob): Promise<Job> {
    const result = await request<Job | { job: Job }>("POST", "/jobs", job);
    return "job" in result ? result.job : result;
  },
  job: (id: string) => request<Job>("GET", `/jobs/${id}`),
  jobs: (limit = 30) => request<Job[]>("GET", `/jobs?limit=${limit}`),
  cancelJob: (id: string) => request<unknown>("POST", `/jobs/${id}/cancel`),
  events: (jobId: string, after = 0) => request<EventsPage>("GET", `/jobs/${jobId}/events?after=${after}`),
  /** `remember` also stops the server asking about this kind of action again. */
  approve: (id: string, options: { note?: string; remember?: boolean } = {}) =>
    request<Approval>("POST", `/approvals/${id}/approve`, options),
  permissions: () => request<Permissions>("GET", "/settings/permissions"),
  setPermissions: (change: { mode?: "default" | "ask"; allowedKinds?: string[] }) =>
    request<Permissions>("PUT", "/settings/permissions", change),
  deny: (id: string, note?: string) => request<Approval>("POST", `/approvals/${id}/deny`, { note }),
  answer: (id: string, selected: string[], text?: string) =>
    request<Question>("POST", `/questions/${id}/answer`, { selected, text }),
  catalogs: () => request<Catalog[]>("GET", "/compliance/catalogs"),
};
