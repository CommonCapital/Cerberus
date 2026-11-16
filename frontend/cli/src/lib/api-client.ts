import { clearAuth, getAuth } from "./auth";
import { ApiError, baseUrl } from "./http-transport";
import { getErrorMessage } from "./http-errors";
import type { ChatTask } from "./task-list";

// The shared auth/session HTTP contract (.NET and TypeScript). No server implementation or
// database package is needed by the CLI. Message parts stay opaque here: the
// chat transport, not the HTTP client, knows the AI SDK's message shape.
export type Credentials = { email: string; password: string };
export type SessionSummary = { id: string; title: string; createdAt: string };
export type SessionData = SessionSummary & {
  userId: string;
  updatedAt: string;
  messages: unknown[];
  /** .NET helper relationship; the TypeScript chat backend does not store it. */
  parentId?: string | null;
  /** Present on a loaded session, omitted when a session has just been made. */
  summarizedThrough?: string | null;
  tasks?: ChatTask[];
};
export type NewSession = { title: string; parentId?: string };

export type ApiClientDeps = {
  baseUrl: () => string;
  getAuth: () => { token: string } | null;
  clearAuth: () => void;
  fetch: (input: string, init: RequestInit) => Promise<Response>;
};

/** Dependencies are read at request time, so changing the server or signing in takes effect at once. */
export function createApiClient(deps: ApiClientDeps) {
  const url = (path: string) => deps.baseUrl().replace(/\/+$/, "") + path;

  async function request(method: string, path: string, body?: unknown): Promise<Response> {
    const address = url(path);
    const auth = deps.getAuth();
    let response: Response;
    try {
      response = await deps.fetch(address, {
        method,
        headers: {
          ...(auth ? { Authorization: `Bearer ${auth.token}` } : {}),
          ...(body === undefined ? {} : { "Content-Type": "application/json" }),
        },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
    } catch {
      throw new ApiError(`Cannot reach the server at ${deps.baseUrl()}. Is the backend running?`, 0);
    }
    if (response.status === 401) deps.clearAuth();
    return response;
  }

  async function json<T>(method: string, path: string, body?: unknown): Promise<T> {
    const response = await request(method, path, body);
    if (!response.ok) throw new ApiError(await getErrorMessage(response), response.status);
    try {
      return await response.json() as T;
    } catch {
      throw new ApiError("The server returned an invalid JSON response.", response.status);
    }
  }

  return {
    // Login deliberately exposes the status: only a 401 may lead to creating
    // a first account. A server error or rate limit must never do that.
    login: (credentials: Credentials) => request("POST", "/auth/login", credentials),
    register: (credentials: Credentials) => request("POST", "/auth/register", credentials),
    sessions: () => json<SessionSummary[]>("GET", "/sessions"),
    session: (id: string) => json<SessionData>("GET", `/sessions/${encodeURIComponent(id)}`),
    createSession: (session: NewSession) => json<SessionData>("POST", "/sessions", session),
    chatUrl: () => url("/chat"),
  };
}

export const apiClient = createApiClient({
  baseUrl,
  getAuth,
  clearAuth,
  fetch: (input, init) => fetch(input, init),
});
