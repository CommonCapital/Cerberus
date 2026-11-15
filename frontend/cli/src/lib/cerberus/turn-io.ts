import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";
import { getAuth } from "../auth";
import { baseUrl } from "./api";
import { turnFetch, turnHeaders, type TurnDeps, type TurnEvent, type TurnHandlers, type TurnInfo, type TurnPage } from "./turn-fetch";

// The real server behind a conversation over the queue (see turn-fetch.ts):
// turns go in over HTTP, replies come back over one SignalR connection to the
// chat hub that all of this client's conversations share. The server sends a
// connection only its own user's turns; here they are sorted by turn.

const listeners = new Map<string, Set<TurnHandlers>>();
let connection: HubConnection | null = null;
let connectedAs: string | null = null;

const authHeaders = (): Record<string, string> => {
  const auth = getAuth();
  return auth ? { Authorization: `Bearer ${auth.token}` } : {};
};

/** One connection for as long as someone listens, to the server and as the user in use right now. */
function connect() {
  const key = `${baseUrl()}|${getAuth()?.token ?? ""}`;
  if (connection && connectedAs === key && connection.state !== HubConnectionState.Disconnected) return;
  void connection?.stop().catch(() => {});

  const hub = new HubConnectionBuilder()
    .withUrl(`${baseUrl()}/hub/chat`, { accessTokenFactory: () => getAuth()?.token ?? "" })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.None)
    .build();
  hub.on("chatEvent", (pushed: { turnId: string } & TurnEvent) => {
    for (const handlers of listeners.get(pushed.turnId) ?? []) handlers.event({ sequence: pushed.sequence, chunk: pushed.chunk });
  });
  hub.on("chatTurn", (turn: { turnId: string }) => {
    for (const handlers of listeners.get(turn.turnId) ?? []) handlers.changed();
  });
  // Back after a break: everyone reads what they missed.
  hub.onreconnected(() => {
    for (const set of listeners.values()) for (const handlers of set) handlers.changed();
  });
  connection = hub;
  connectedAs = key;
  // No push channel is not an error: the stored turn and the poll still give the whole reply.
  void hub.start().then(
    () => {
      for (const set of listeners.values()) for (const handlers of set) handlers.changed();
    },
    () => {},
  );
}

export const turnIo: TurnDeps = {
  submit: async (body, headers) => {
    const response = await fetch(`${baseUrl()}/chat/turns`, { method: "POST", headers: turnHeaders(headers, authHeaders()), body });
    if (response.status !== 202) return { refused: response };
    return { turn: (await response.json()) as TurnInfo };
  },
  events: async (turnId, after) => {
    const response = await fetch(`${baseUrl()}/chat/turns/${turnId}/events?after=${after}`, { headers: authHeaders() });
    if (!response.ok) throw new Error(`The turn could not be read (${response.status}).`);
    return (await response.json()) as TurnPage;
  },
  cancel: (turnId) => fetch(`${baseUrl()}/chat/turns/${turnId}/cancel`, { method: "POST", headers: authHeaders() }),
  subscribe: (turnId, handlers) => {
    const set = listeners.get(turnId) ?? new Set<TurnHandlers>();
    set.add(handlers);
    listeners.set(turnId, set);
    connect();
    return () => {
      set.delete(handlers);
      if (set.size === 0) listeners.delete(turnId);
    };
  },
  every: (ms, callback) => {
    const timer = setInterval(callback, ms);
    return () => clearInterval(timer);
  },
};

/**
 * What the chat transport uses as `fetch`: the conversation over the queue, unless the operator asked for the
 * direct stream (CERBERUS_CHAT_TRANSPORT=http) — which the server still offers, for a server too old to queue.
 */
export const chatFetch = (): typeof fetch | undefined =>
  process.env.CERBERUS_CHAT_TRANSPORT?.toLowerCase() === "http" ? undefined : turnFetch(turnIo);
