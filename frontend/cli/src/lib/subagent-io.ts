import { DefaultChatTransport, type UIMessage } from "ai";
import { apiClient } from "./api-client";
import { getAuth } from "./auth";
import { chatFetch } from "./cerberus/turn-io";
import { projectContext } from "./project-context";
import type { HelperDeps } from "./subagent";

// The real server behind a helper (see subagent.ts): its conversation is a
// child session there, and its replies come over the same chat endpoint as
// the operator's own chat.

export const helperIo: Pick<HelperDeps, "createSession" | "transport"> = {
  createSession: async (title, parentId) => {
    return (await apiClient.createSession({ title, parentId })).id;
  },
  transport: (sessionId, model) =>
    new DefaultChatTransport<UIMessage>({
      api: apiClient.chatUrl(),
      // A helper's turns travel like the operator's own: queued, answered by a worker, delivered over SignalR.
      fetch: chatFetch(),
      headers() {
        const auth = getAuth();
        return auth ? { Authorization: `Bearer ${auth.token}` } : new Headers();
      },
      prepareSendMessagesRequest({ messages }) {
        const last = messages[messages.length - 1];
        const previous = messages[messages.length - 2];
        return {
          body: {
            id: sessionId,
            // A reply being continued goes back with the message it answers; a new prompt goes alone.
            messages: last?.role === "assistant" && previous ? [previous, last] : last ? [last] : [],
            // The server gives a helper the read-only tools whatever is said here; this says the same.
            mode: "PLAN",
            model,
            context: projectContext(),
          },
        };
      },
    }),
};
