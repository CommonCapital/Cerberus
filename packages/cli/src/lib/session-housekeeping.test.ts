import { expect, test } from "bun:test";
import { forkPoints, saveTranscript, transcriptFileName } from "./session-housekeeping";

const user = (id: string, text: string, metadata?: { shell?: unknown; note?: unknown }) => ({ id, role: "user", parts: [{ type: "text", text }], ...(metadata ? { metadata } : {}) });
const reply = (id: string) => ({ id, role: "assistant", parts: [{ type: "text", text: "ok" }] });

test("a conversation can be forked whole, or after the reply to an earlier prompt, latest first", () => {
  const points = forkPoints([user("u1", "Find the login bug."), reply("a1"), user("u2", "Now fix\n it."), reply("a2"), user("u3", "Add a test."), reply("a3")]);

  expect(points).toEqual([
    { key: "whole", label: "The whole conversation", hint: "6 messages" },
    { key: "a2", label: "Up to the reply to: Now fix it.", hint: "prompt 2 of 3", through: "a2" },
    { key: "a1", label: "Up to the reply to: Find the login bug.", hint: "prompt 1 of 3", through: "a1" },
  ]);
});

test("a conversation with one exchange, or none, can only be forked whole", () => {
  expect(forkPoints([])).toEqual([{ key: "whole", label: "The whole conversation", hint: "0 messages" }]);
  expect(forkPoints([user("u1", "Hello"), reply("a1")]).map((p) => p.key)).toEqual(["whole"]);
});

test("a command the operator ran or a note is part of the exchange it happened in, not a prompt", () => {
  const points = forkPoints([
    user("u1", "Find the bug."), reply("a1"),
    user("s1", "$ git status", { shell: { command: "git status" } }),
    user("n1", "The last change was undone.", { note: true }),
    user("u2", "Fix it."), reply("a2"),
  ]);

  expect(points.map((p) => p.key)).toEqual(["whole", "a1"]);
  expect(points[1]!.hint).toBe("prompt 1 of 2");
});

test("a prompt that got no reply is not a place to fork at; a reply in several messages ends at the last", () => {
  const points = forkPoints([user("u1", "One"), reply("a1"), reply("a1b"), user("u2", "Two"), user("u3", "Three"), reply("a3")]);

  expect(points.map((p) => p.key)).toEqual(["whole", "a1b"]);
});

test("a long prompt is shown by its beginning", () => {
  const points = forkPoints([user("u1", "x".repeat(200)), reply("a1"), user("u2", "y"), reply("a2")]);

  expect(points[1]!.label).toBe(`Up to the reply to: ${"x".repeat(70)}…`);
});

test("a transcript is fetched and written into the current directory under the session's name", async () => {
  const written: { path: string; content: string }[] = [];

  const path = await saveTranscript("0123456789abcdef", {
    fetch: async (id) => `# Chat ${id}\n`,
    write: (p, content) => void written.push({ path: p, content }),
    directory: () => "/work/project",
  });

  expect(path).toBe("/work/project/cerberus-chat-01234567.md");
  expect(written).toEqual([{ path, content: "# Chat 0123456789abcdef\n" }]);
  expect(transcriptFileName("abc")).toBe("cerberus-chat-abc.md");
});

test("a transcript that cannot be fetched writes nothing", async () => {
  const written: string[] = [];

  await expect(
    saveTranscript("s1", {
      fetch: async () => {
        throw new Error("Session not found");
      },
      write: (p) => void written.push(p),
      directory: () => "/work",
    }),
  ).rejects.toThrow("Session not found");
  expect(written).toEqual([]);
});
