import { useState, useEffect, useMemo, useRef } from "react";
import { useParams, useLocation, useNavigate } from "react-router";
import { z } from "zod";
import { TextAttributes } from "@opentui/core";
import { useKeyboard } from "@opentui/react";
import { type ModeType, type SupportedChatModelId } from "@cerberus/shared";
import type { InferResponseType } from "hono/client";
import { SessionShell } from "../components/session-shell";
import { 
  UserMessage, 
  BotMessage, 
  ErrorMessage
} from "../components/messages";
import { useToast } from "../providers/toast";
import { describeCompaction, describeRetry, useChat } from "../hooks/use-chat";
import { TaskStrip } from "../components/task-strip";
import { latestTasks } from "../lib/task-list";
import { isIdle, readyToName, shellCommandOf } from "../lib/chat-flow";
import { toggleToolCalls } from "../lib/tool-fold-state";
import { cerberus } from "../lib/cerberus/api";
import { onUndoRequest } from "../lib/undo-events";
import { describeOutcome, journalFor } from "../lib/undo-journal";
import { ShellMessage } from "../components/messages/shell-message";
import { usePromptConfig } from "../providers/prompt-config";
import type { Message } from "../hooks/use-chat";
import { apiClient } from "../lib/api-client";
import { getErrorMessage } from "../lib/http-errors";
import { useKeyboardLayer } from "../providers/keyboard-layer";
import { ToolPermissionPrompt, useToolPermission } from "../providers/tool-permission";

type SessionData = InferResponseType<(typeof apiClient.sessions)[":id"]["$get"], 200>;

const sessionLocationSchema = z.object({
  session: z.custom<SessionData>((val) => val != null && typeof val === "object" && "id" in val),
  initialPrompt: z
    .object({
      message: z.string(),
      mode: z.custom<ModeType>(),
      model: z.custom<SupportedChatModelId>(),
    })
    .optional(),
});

function ChatMessage(
  { msg, streaming = false }: {
    msg: Message;
    /** The reply is still being written. */
    streaming?: boolean;
  }
) {
  if (msg.role === "user" && msg.metadata?.shell) {
    return <ShellMessage record={msg.metadata.shell} />;
  }

  // Something done outside the chat that the agent is told about (an undo): shown as a remark, not as a prompt.
  if (msg.role === "user" && msg.metadata?.note) {
    const text = msg.parts.filter((p) => p.type === "text").map((p) => p.text).join("");
    return (
      <box paddingX={3} width="100%">
        <text attributes={TextAttributes.DIM}>↺ {text}</text>
      </box>
    );
  }

  if (msg.role === "user") {
    const text = msg.parts
      .filter((p) => p.type === "text")
      .map((p) => p.text)
      .join("");

    return <UserMessage message={text} mode={msg.metadata?.mode ?? "BUILD"} />;
  }

  return (
    <BotMessage
      parts={msg.parts}
      model={msg.metadata?.model ?? "unknown"}
      mode={msg.metadata?.mode ?? "BUILD"}
      durationMs={msg.metadata?.durationMs}
      streaming={streaming}
    />
  );
};

function SessionChat({ 
  session,
  initialPrompt,
}: { 
  session: SessionData,
  initialPrompt?: { message: string; mode: ModeType; model: SupportedChatModelId };
}) {
  const [initialMessages] = useState(() => session.messages as unknown as Message[]);
  const { mode, model } = usePromptConfig();
  const { isTopLayer } = useKeyboardLayer();
  const { ask, denyAll } = useToolPermission();
  const { messages, status, submit, abort, interrupt, error, retry, compacted, runShell, addNote } = useChat(
    session.id,
    initialMessages,
    ask,
  );
  const toast = useToast();
  // Prompts typed while the agent was busy; each is sent when the conversation comes to rest.
  const [queued, setQueued] = useState<string[]>([]);
  // The operator's own command, while it runs.
  const [running, setRunning] = useState<string | null>(null);
  const idle = isIdle(messages, status) && running === null;

  // One queued prompt at a time: the next is held back until the one just sent has visibly started.
  const dispatching = useRef(false);
  useEffect(() => {
    if (status !== "ready") dispatching.current = false;
  }, [status]);

  useEffect(() => {
    if (!idle || queued.length === 0 || error || dispatching.current) return;
    const [next, ...rest] = queued;
    dispatching.current = true;
    setQueued(rest);
    void submit({ userText: next!, mode, model });
  }, [idle, queued, error, submit, mode, model]);

  // A new conversation is named by the model once its first reply is in. An opened one keeps its name.
  // A title is a nicety: if it cannot be written, the first words of the prompt stay.
  const named = useRef(initialMessages.length > 0);
  useEffect(() => {
    if (named.current || error || !readyToName(messages, status)) return;
    named.current = true;
    void cerberus.nameSession(session.id).catch(() => {});
  }, [messages, status, error, session.id]);

  // "/undo" and "/redo" for this conversation: carried out here, where it is known whether the agent is at rest.
  const undoState = useRef({ idle, mode, model });
  undoState.current = { idle, mode, model };
  useEffect(
    () =>
      onUndoRequest(session.id, (kind) => {
        const now = undoState.current;
        if (!now.idle) {
          toast.show({ variant: "error", message: `Wait for the reply to end before you ${kind}.` });
          return;
        }
        try {
          const outcome = journalFor(session.id)[kind](process.cwd());
          if (outcome === null) {
            toast.show({ variant: "info", message: kind === "undo" ? "Nothing to undo: the agent has changed no files here." : "Nothing to redo." });
            return;
          }
          toast.show({ variant: outcome.restored.length > 0 ? "info" : "error", message: describeOutcome(kind, outcome) });
          // The agent still believes its changes stand; it is told with the next prompt.
          if (outcome.restored.length > 0) addNote(describeOutcome(kind, outcome, true), now.mode, now.model);
        } catch (problem) {
          toast.show({ variant: "error", message: problem instanceof Error ? problem.message : `Could not ${kind}` });
        }
      }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [session.id],
  );

  const handleSubmit = (text: string) => {
    const command = shellCommandOf(text);
    if (command !== null) {
      if (!idle) {
        toast.show({ variant: "error", message: "Wait for the reply to end before running a command of your own." });
        return;
      }
      setRunning(command);
      runShell(command, mode, model)
        .catch((problem: unknown) => toast.show({ variant: "error", message: problem instanceof Error ? problem.message : "The command could not be run" }))
        .finally(() => setRunning(null));
      return;
    }
    if (idle) void submit({ userText: text, mode, model });
    else setQueued((current) => [...current, text]);
  };
  const hasSubmittedInitialPromptRef = useRef(false);

  // Stop the pending reply when the user leaves this session.
  useEffect(() => {
    return () => {
      denyAll();   // nobody is left to answer a pending tool question
      void abort();
    };
  }, [abort, denyAll]);

  // Let the user cancel a reply even before the first streamed chunk arrives.
  useKeyboard((key) => {
    if (key.name === "escape" && isTopLayer("base") && status === "streaming") {
      key.preventDefault();
      interrupt();
      setQueued([]);   // the operator took the wheel: what was lined up no longer follows
    }
    // Folded runs of the agent's reads and searches, shown in full and folded again.
    if (key.ctrl && key.name === "o" && isTopLayer("base")) {
      key.preventDefault();
      toggleToolCalls();
    }
  });

  useEffect(() => {
    if (!initialPrompt || hasSubmittedInitialPromptRef.current) return;
    hasSubmittedInitialPromptRef.current = true;
    void submit({
      userText: initialPrompt.message,
      mode: initialPrompt.mode,
      model: initialPrompt.model,
    });
  }, [initialPrompt, submit]);

  return (
    <SessionShell
      onSubmit={handleSubmit}
      loading={status === "submitted" || status === "streaming"}
      interruptible={status === "submitted" || status === "streaming"}
    >
      {messages.map((msg, index) => (
        <ChatMessage key={msg.id} msg={msg} streaming={status === "streaming" && index === messages.length - 1} />
      ))}
      {running !== null ? <text attributes={TextAttributes.DIM}>$ {running} …</text> : null}
      <TaskStrip tasks={latestTasks(messages)} />
      {queued.map((text, index) => (
        <text key={index} attributes={TextAttributes.DIM}>
          queued: {text.length > 100 ? `${text.slice(0, 100)}…` : text}
          {index === 0 ? "  (sent when the reply ends; esc drops it)" : ""}
        </text>
      ))}
      {compacted ? <text attributes={TextAttributes.DIM}>{describeCompaction(compacted)}</text> : null}
      {retry ? <text attributes={TextAttributes.DIM}>{describeRetry(retry)}</text> : null}
      {error && <ErrorMessage message={error.message} />}
      <ToolPermissionPrompt />
    </SessionShell>
  );
}

export function Session() {
  const { id } = useParams();
  const location = useLocation();
  const navigate = useNavigate();
  const toast = useToast();

  const prefetched = useMemo(() => {
    const parsed = sessionLocationSchema.safeParse(location.state);
    return parsed.success ? parsed.data : null;
  }, [location.state]);

  const [session, setSession] = useState<SessionData | null>(prefetched?.session ?? null);

  useEffect(() => {
    // Skip fetch if session was passed via location state
    if (prefetched?.session) return;

    setSession(null);

    if (!id) return;

    let ignore = false;
    const fetchSession = async () => {
      try {
        const res = await apiClient.sessions[":id"].$get({ 
          param: { id },
        });
        if (ignore) return;
        if (!res.ok) throw new Error(await getErrorMessage(res));
        const resolved = await res.json();
        setSession(resolved);
      } catch (err) {
        if (ignore) return;
        toast.show({
          variant: "error",
          message: err instanceof Error ? err.message : "Failed to load session",
        });
        navigate("/", { replace: true });
      }
    };

    fetchSession();
    return () => {
      ignore = true;
    };
  }, [id, prefetched, toast, navigate]);

  if (!session) {
    return <SessionShell onSubmit={() => {}} inputDisabled loading />;
  }

  return (
    <SessionChat 
      key={session.id} 
      session={session} 
      initialPrompt={prefetched?.initialPrompt}
    />
  );
};
