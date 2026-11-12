import type { CommandResult } from "./local-tools";

// What happens to a line typed into the chat besides "send it to the model":
// a line starting with "!" is a command the operator runs themself, and a
// line typed while the agent is busy waits its turn.

/** The command of a "!" line, or null when the line is not one. */
export function shellCommandOf(text: string): string | null {
  if (!text.startsWith("!")) return null;
  const command = text.slice(1).trim();
  return command.length > 0 ? command : null;
}

/** How much of a command's output goes into the conversation; the rest is in the saved file the output names. */
const MAX_OUTPUT_IN_CHAT = 8_000;

export type ShellRecord = { command: string; exitCode: number; output: string; timedOut?: boolean };

const clipMiddle = (text: string, limit: number) =>
  text.length <= limit
    ? text
    : `${text.slice(0, Math.floor(limit / 4))}\n[… ${text.length - limit} characters left out …]\n${text.slice(-(limit - Math.floor(limit / 4)))}`;

/** What a finished command is remembered as: its output as one text, clipped. */
export function shellRecord(command: string, result: CommandResult): ShellRecord {
  const output = [result.stdout.trimEnd(), result.stderr.trimEnd()].filter((part) => part.length > 0).join("\n");
  return {
    command,
    exitCode: result.exitCode,
    output: clipMiddle(output, MAX_OUTPUT_IN_CHAT),
    ...(result.timedOut ? { timedOut: true } : {}),
  };
}

/**
 * The same, as the model reads it: said by the user, because it is the user
 * who ran it — the model did not, and should not think it did.
 */
export function shellMessageText(record: ShellRecord): string {
  const ended = record.timedOut ? "it was stopped: it ran too long" : `exit code ${record.exitCode}`;
  return (
    `I ran this command myself in the project directory:\n$ ${record.command}\n\n` +
    `${record.output.length > 0 ? record.output : "(it printed nothing)"}\n\n(${ended})`
  );
}

type Part = { type: string; state?: string };
type Msg = { id: string; role: string; parts: readonly Part[] };

/**
 * Whether the conversation is at rest: no reply is being produced, and the
 * last reply is not waiting for a tool to run or for the operator's say-so.
 * Only then may the next prompt be sent.
 */
export function isIdle(messages: readonly Msg[], status: string): boolean {
  if (status !== "ready") return false;
  const last = messages.at(-1);
  if (!last || last.role !== "assistant") return true;
  return !last.parts.some(
    (part) => (part.type.startsWith("tool-") || part.type === "dynamic-tool") && part.state !== "output-available" && part.state !== "output-error",
  );
}

/**
 * The messages to send with `messages[index]`: it, preceded by the local-only
 * messages right before it that the server has not seen yet (commands the
 * operator ran), in order.
 */
export function withUnsent<T extends { id: string }>(messages: readonly T[], index: number, unsent: ReadonlySet<string>): T[] {
  let start = index;
  while (start > 0 && unsent.has(messages[start - 1]!.id)) start--;
  return messages.slice(start, index + 1);
}
