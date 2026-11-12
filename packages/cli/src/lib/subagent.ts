import { Chat } from "@ai-sdk/react";
import type { ChatTransport, UIMessage } from "ai";
import { summarizeToolCall } from "./tool-display";

// A helper: a second conversation with the model, in its own context, that
// the chat agent hands a question to (the `task` tool). The helper looks —
// read-only tools — and its last message is its report; that report is all
// the agent gets back, so what the helper read along the way does not fill
// the agent's context. Its file tools run here, on the operator's machine,
// which is why this side runs the conversation. Everything that touches the
// server or the disk is passed in.

/** The tools a helper may use on this machine. The server offers it no others; this is the second lock. */
export const HELPER_TOOLS = ["readFile", "listDirectory", "glob", "grep"] as const;

/** How many times the helper may go back to the model with tool results before it is stopped. */
export const MAX_ROUNDS = 25;

/** A report longer than this is cut: the point of a helper is a short answer. */
export const MAX_REPORT = 20_000;

export type HelperOutput = {
  /** The helper's last message. */
  report: string;
  toolCalls: number;
  /** False when it was stopped before it finished; `note` says why. */
  complete: boolean;
  note?: string;
};

export type HelperTask = {
  parentSessionId: string;
  description: string;
  prompt: string;
  model: string;
  signal?: AbortSignal;
};

export type HelperDeps = {
  /** Creates the helper's conversation on the server, under the chat that started it; returns its id. */
  createSession: (title: string, parentId: string) => Promise<string>;
  /** The connection to the model for that conversation. */
  transport: (sessionId: string, model: string) => ChatTransport<UIMessage>;
  /** Decides and runs one tool call of the helper. Returns its output, or throws with a message for the model. */
  runTool: (toolName: string, input: unknown) => Promise<unknown>;
  /** What the helper is doing, as it goes. */
  onProgress?: (text: string) => void;
};

type Part = UIMessage["parts"][number];
type PendingCall = { toolName: string; toolCallId: string; input: unknown };

const isTool = (part: Part) => part.type === "dynamic-tool" || part.type.startsWith("tool-");

/** The tool calls of a reply that are waiting for this side to run them. */
export function pendingCalls(message: UIMessage | undefined): PendingCall[] {
  if (!message || message.role !== "assistant") return [];
  const calls: PendingCall[] = [];
  for (const part of message.parts) {
    if (!isTool(part)) continue;
    const tool = part as { type: string; toolName?: string; toolCallId: string; state: string; input?: unknown; providerExecuted?: boolean };
    if (tool.state !== "input-available" || tool.providerExecuted) continue;
    calls.push({
      toolName: tool.type === "dynamic-tool" ? (tool.toolName ?? "") : tool.type.slice("tool-".length),
      toolCallId: tool.toolCallId,
      input: tool.input,
    });
  }
  return calls;
}

/** What the helper ended on: the text of its last step, or of the whole reply if that step said nothing. */
export function reportOf(message: UIMessage | undefined): string {
  if (!message || message.role !== "assistant") return "";
  const texts = (parts: Part[]) =>
    parts.filter((part): part is Extract<Part, { type: "text" }> => part.type === "text").map((part) => part.text).join("").trim();
  const lastStep = message.parts.findLastIndex((part) => part.type === "step-start");
  return texts(message.parts.slice(lastStep + 1)) || texts(message.parts);
}

const clip = (text: string, max: number) => (text.length <= max ? text : `${text.slice(0, max)}\n[… the report was cut here: ${text.length - max} more characters]`);

const oneLine = (text: string) => text.replace(/\s+/g, " ").trim();

/** Runs a helper on one task and returns its report. Throws when it cannot be started, fails, or is interrupted. */
export async function runHelper(task: HelperTask, deps: HelperDeps): Promise<HelperOutput> {
  const prompt = task.prompt.trim();
  if (prompt.length === 0) throw new Error("The task has no prompt: say what the helper is to find out.");
  const description = oneLine(task.description).slice(0, 80) || "Helper";
  const interrupted = () => new Error("The helper was interrupted before it finished; it has no report.");
  if (task.signal?.aborted) throw interrupted();

  const sessionId = await deps.createSession(description, task.parentSessionId);
  const chat = new Chat<UIMessage>({ id: sessionId, transport: deps.transport(sessionId, task.model) });
  const stop = () => void chat.stop();
  task.signal?.addEventListener("abort", stop, { once: true });

  let toolCalls = 0;
  try {
    deps.onProgress?.("starting");
    await chat.sendMessage({ text: prompt });

    for (let round = 0; ; round++) {
      if (task.signal?.aborted) throw interrupted();
      if (chat.status === "error") throw new Error(`The helper failed: ${chat.error?.message ?? "the model could not be reached"}`);

      const calls = pendingCalls(chat.lastMessage);
      if (calls.length === 0) break;
      if (round >= MAX_ROUNDS) {
        return {
          report: clip(reportOf(chat.lastMessage), MAX_REPORT),
          toolCalls,
          complete: false,
          note: `The helper was stopped after ${MAX_ROUNDS} rounds of tool calls without finishing. What it had written so far is above; it may be incomplete.`,
        };
      }

      for (const call of calls) {
        if (task.signal?.aborted) throw interrupted();
        toolCalls++;
        deps.onProgress?.(`${toolCalls} tool call${toolCalls === 1 ? "" : "s"} · ${call.toolName} ${summarizeToolCall(call.toolName, call.input)}`.trimEnd());
        try {
          const output = await deps.runTool(call.toolName, call.input);
          await chat.addToolOutput({ tool: call.toolName as never, toolCallId: call.toolCallId, output: output as never });
        } catch (problem) {
          await chat.addToolOutput({
            tool: call.toolName as never,
            toolCallId: call.toolCallId,
            state: "output-error",
            errorText: problem instanceof Error ? problem.message : String(problem),
          });
        }
      }
      // The same reply, continued with the results.
      await chat.sendMessage();
    }

    const report = reportOf(chat.lastMessage);
    if (report.length === 0) {
      return { report: "", toolCalls, complete: false, note: "The helper ended without writing a report." };
    }
    return { report: clip(report, MAX_REPORT), toolCalls, complete: true };
  } finally {
    task.signal?.removeEventListener("abort", stop);
  }
}
