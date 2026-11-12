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
import { executeLocalTool, runCommand } from "../lib/local-tools";
import { shellMessageText, shellRecord, withUnsent, type ShellRecord } from "../lib/chat-flow";
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
    const { mode, allowedKinds, allowedCommands } = await cerberus.permissions();
    return { mode, allowedKinds, allowedCommands: allowedCommands ?? [] };
  } catch {
    return STANDARD_POLICY;
  }
}

export type ChatMessageMetadata = {
  mode?: ModeType;
  model?: SupportedChatModelId | string;
  durationMs?: number;
  usage?: LanguageModelUsage;
  /** Set on a message that records a command the operator ran themself with "!". */
  shell?: ShellRecord;
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

/** What the operator is told when the server made room by summarising. */
export const describeCompaction = (messages: number) =>
  `To make room, ${messages} earlier message${messages === 1 ? " was" : "s were"} summarised for the model. Nothing is removed from this screen.`;

export type Message = UIMessage<ChatMessageMetadata, { retry: RetryNotice; compaction: { messages: number } }, ChatTools>;

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
  // Messages added on this side only (commands the operator ran); they go to the server with the next prompt.
  const unsentRef = useRef<Set<string>>(new Set());
  const [retry, setRetry] = useState<RetryNotice | null>(null);
  // How many messages the server summarised before the latest reply; shown with that reply.
  const [compacted, setCompacted] = useState<number | null>(null);

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
            // A new prompt takes along what the operator ran themself since the last one.
            : withUnsent(messages, messages.length - 1, unsentRef.current);

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
      else if (part.type === "data-compaction") setCompacted(part.data.messages);
    },
    onFinish() {
      setRetry(null);
      unsentRef.current.clear();   // the server has them now
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
        if (gate.remembered || gate.rememberedCommands) {
          policyRef.current = Promise.resolve(gate.policy);
          // Remembered on the server too, so it holds in later chats. If that fails it still holds for this one.
          void cerberus
            .setPermissions(gate.rememberedCommands ? { allowedCommands: gate.policy.allowedCommands } : { allowedKinds: gate.policy.allowedKinds })
            .catch(() => {});
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
    /** Set when the server summarised earlier messages to fit the model's window; stays until the next message is sent. */
    compacted,
    submit: (params: { userText: string; mode: ModeType; model: SupportedChatModelId }) => {
      setCompacted(null);
      return chat.sendMessage({
        text: params.userText,
        metadata: {
          mode: params.mode,
          model: params.model,
        },
      })
    },
    /**
     * Runs a command of the operator's own and records it in the conversation,
     * so the agent sees what was run and what came of it with the next prompt.
     */
    runShell: async (command: string, mode: ModeType, model: SupportedChatModelId) => {
      const record = shellRecord(command, await runCommand(command, { byAgent: false }));
      const id = crypto.randomUUID();
      unsentRef.current.add(id);
      chat.setMessages((current) => [
        ...current,
        { id, role: "user", parts: [{ type: "text", text: shellMessageText(record) }], metadata: { mode, model, shell: record } },
      ]);
      return record;
    },
    abort: chat.stop,
    interrupt: chat.stop,
  };
};
