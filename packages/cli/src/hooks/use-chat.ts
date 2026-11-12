import { useMemo, useRef, useState } from "react";
import { useChat as useAiChat } from "@ai-sdk/react";
import {
  DefaultChatTransport,
  type InferUITools,
  lastAssistantMessageIsCompleteWithToolCalls,
  type LanguageModelUsage,
  type UIMessage,
} from "ai";
import { type ModeType, type SupportedChatModelId, type ToolContracts } from "@nightcode/shared";
import { apiClient } from "../lib/api-client";
import { getAuth } from "../lib/auth";
import { executeLocalTool } from "../lib/local-tools";
import { projectContext } from "../lib/project-context";
import { cerberus } from "../lib/cerberus/api";
import {
  deniedMessage,
  gateToolCall,
  STANDARD_POLICY,
  type Policy,
} from "../lib/cerberus/tool-permissions";
import type { ToolDecision, ToolRequest } from "../providers/tool-permission";

/**
 * The permission policy from the server. A server without the endpoint (or one
 * that cannot be reached) gets the standard policy, which asks before any
 * change — never the permissive option.
 */
async function loadPolicy(): Promise<Policy> {
  try {
    const { mode, allowedKinds } = await cerberus.permissions();
    return { mode, allowedKinds };
  } catch {
    return STANDARD_POLICY;
  }
}

export type ChatMessageMetadata = {
  mode?: ModeType;
  model?: SupportedChatModelId | string;
  durationMs?: number;
  usage?: LanguageModelUsage;
};

type ChatTools = {
  [Name in keyof InferUITools<ToolContracts>]: {
    input: InferUITools<ToolContracts>[Name]["input"];
    output: unknown;
  };
};

/** The provider was busy; the server waits and tries again. Shown while it lasts, never stored. */
export type RetryNotice = { attempt: number; maxAttempts: number; seconds: number; reason: string };

/** What a retry notice says to the operator. */
export const describeRetry = (retry: RetryNotice) =>
  `The model provider is busy (${retry.reason}). Trying again in ${retry.seconds} s — attempt ${retry.attempt} of ${retry.maxAttempts}.`;

export type Message = UIMessage<ChatMessageMetadata, { retry: RetryNotice }, ChatTools>;

/** Whether the reply in progress has produced anything yet (after which a retry notice is stale). */
function lastHasContent(messages: Message[]): boolean {
  const last = messages.at(-1);
  return last?.role === "assistant" && last.parts.some((part) => part.type === "text" || part.type === "reasoning" || part.type.startsWith("tool-"));
}

export function useChat(
  sessionId: string,
  initialMessages: Message[],
  askOperator: (request: ToolRequest) => Promise<ToolDecision>,
) {
  // Read once per chat, then kept current as the operator chooses "always allow".
  const policyRef = useRef<Promise<Policy> | null>(null);
  const [retry, setRetry] = useState<RetryNotice | null>(null);

  const transport = useMemo(() => {
    return new DefaultChatTransport<Message>({
      api: apiClient.chat.$url().toString(),
      headers() {
        const auth = getAuth();
        return auth ? { Authorization: `Bearer ${auth.token}` } : new Headers();
      },
      prepareSendMessagesRequest({ messages }) {
        const message = messages[messages.length - 1];
        if (!message) throw new Error("No message to send");

        const metadata = messages.findLast(
          (m) => m.metadata?.mode && m.metadata?.model,
        )?.metadata;
        const previousMessage = messages[messages.length - 2];
        const requestMessages =
          message.role === "assistant" && previousMessage?.role === "user"
            ? [previousMessage, message]
            : [message];

        return {
          body: {
            id: sessionId,
            messages: requestMessages,
            mode: message.metadata?.mode ?? metadata?.mode,
            model: message.metadata?.model ?? metadata?.model,
            // Where the tools run, and the project's instructions file — read fresh each time.
            context: projectContext(),
          },
        }
      }
    });
  }, [sessionId]);

  const chat = useAiChat<Message>({
    id: sessionId,
    messages: initialMessages,
    transport,
    onData(part) {
      if (part.type === "data-retry") setRetry(part.data);
    },
    onFinish() {
      setRetry(null);
    },
    onError() {
      setRetry(null);
    },
    onToolCall({ toolCall }) {
      const mode = chat.messages.at(-1)?.metadata?.mode ?? "BUILD";
      const tool = toolCall.toolName as keyof ChatTools;
      const fail = (errorText: string) =>
        chat.addToolOutput({ tool, toolCallId: toolCall.toolCallId, state: "output-error", errorText });

      void (async () => {
        // Human in the loop: nothing that needs the operator's say-so runs without it.
        policyRef.current ??= loadPolicy();
        const gate = await gateToolCall(await policyRef.current, toolCall.toolName, toolCall.input, askOperator);
        if (!gate.run) {
          fail(deniedMessage(gate.feedback));
          return;
        }
        if (gate.remembered) {
          policyRef.current = Promise.resolve(gate.policy);
          // Remembered on the server too, so it holds in later chats. If that fails it still holds for this one.
          void cerberus.setPermissions({ allowedKinds: gate.policy.allowedKinds }).catch(() => {});
        }

        try {
          const output = await executeLocalTool(toolCall.toolName, toolCall.input, mode);
          chat.addToolOutput({ tool, toolCallId: toolCall.toolCallId, output });
        } catch (error) {
          fail(error instanceof Error ? error.message : String(error));
        }
      })();
    },
    sendAutomaticallyWhen: lastAssistantMessageIsCompleteWithToolCalls,
  });

  return {
    messages: chat.messages,
    status: chat.status,
    error: chat.error,
    /** Set while the server is waiting to retry a busy provider; cleared once content arrives or the reply ends. */
    retry: chat.status === "streaming" && lastHasContent(chat.messages) ? null : retry,
    submit: (params: { userText: string; mode: ModeType; model: SupportedChatModelId }) => {
      return chat.sendMessage({
        text: params.userText,
        metadata: {
          mode: params.mode,
          model: params.model,
        },
      })
    },
    abort: chat.stop,
    interrupt: chat.stop,
  };
};
