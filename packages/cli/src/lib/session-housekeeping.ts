import { join } from "node:path";

// Looking after conversations: where one can be forked, and saving one as a
// file. Pure; what touches the server and the disk is passed in.

type Part = { type: string; text?: string };
type Msg = { id: string; role: string; parts: readonly Part[]; metadata?: { shell?: unknown; note?: unknown } | null };

export type ForkPoint = {
  key: string;
  label: string;
  hint: string;
  /** The reply the fork ends with; undefined for the whole conversation. */
  through?: string;
};

const clip = (text: string, max: number) => {
  const oneLine = text.replace(/\s+/g, " ").trim();
  return oneLine.length <= max ? oneLine : `${oneLine.slice(0, max)}…`;
};

const promptOf = (message: Msg): string | null => {
  if (message.role !== "user" || message.metadata?.shell || message.metadata?.note) return null;
  const text = message.parts.filter((part) => part.type === "text").map((part) => part.text ?? "").join("").trim();
  return text.length > 0 ? text : null;
};

/**
 * Where a conversation can be forked: as a whole, or after the reply to one
 * of the earlier prompts — latest first. A fork always ends with a reply; a
 * prompt that got none is not a place to carry on from.
 */
export function forkPoints(messages: readonly Msg[]): ForkPoint[] {
  const points: ForkPoint[] = [
    { key: "whole", label: "The whole conversation", hint: `${messages.length} message${messages.length === 1 ? "" : "s"}` },
  ];

  const exchanges: { prompt: string; reply: string | null; number: number }[] = [];
  for (const message of messages) {
    const prompt = promptOf(message);
    if (prompt !== null) exchanges.push({ prompt, reply: null, number: exchanges.length + 1 });
    else if (message.role === "assistant" && exchanges.length > 0) exchanges[exchanges.length - 1]!.reply = message.id;
  }

  const lastId = messages.at(-1)?.id;
  for (const exchange of exchanges.reverse()) {
    // Ending at the conversation's last message is the whole conversation, listed above.
    if (exchange.reply === null || exchange.reply === lastId) continue;
    points.push({
      key: exchange.reply,
      label: `Up to the reply to: ${clip(exchange.prompt, 70)}`,
      hint: `prompt ${exchange.number} of ${exchanges.length}`,
      through: exchange.reply,
    });
  }
  return points;
}

export const transcriptFileName = (sessionId: string) => `cerberus-chat-${sessionId.slice(0, 8)}.md`;

export type TranscriptIo = {
  fetch: (sessionId: string) => Promise<string>;
  write: (path: string, content: string) => void;
  directory: () => string;
};

/** Fetches the conversation as Markdown and writes it into the current directory. Returns the path written. */
export async function saveTranscript(sessionId: string, io: TranscriptIo): Promise<string> {
  const markdown = await io.fetch(sessionId);
  const path = join(io.directory(), transcriptFileName(sessionId));
  io.write(path, markdown);
  return path;
}
