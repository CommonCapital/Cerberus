import { describe, expect, test } from "bun:test";
import { isIdle, shellCommandOf, shellMessageText, shellRecord, withUnsent } from "./chat-flow";

describe("shellCommandOf", () => {
  test("a line starting with ! is a command of the operator's own", () => {
    expect(shellCommandOf("!git status")).toBe("git status");
    expect(shellCommandOf("!  npm test  ")).toBe("npm test");
  });

  test("anything else is a prompt, and a bare ! is nothing to run", () => {
    expect(shellCommandOf("fix the bug!")).toBeNull();
    expect(shellCommandOf(" !ls")).toBeNull();
    expect(shellCommandOf("!")).toBeNull();
    expect(shellCommandOf("!   ")).toBeNull();
  });
});

describe("a command the operator ran", () => {
  test("is recorded with what it printed and how it ended", () => {
    const record = shellRecord("npm test", { stdout: "2 passed\n", stderr: "warning: slow\n", exitCode: 0 });

    expect(record).toEqual({ command: "npm test", exitCode: 0, output: "2 passed\nwarning: slow" });
    expect(shellMessageText(record)).toBe(
      "I ran this command myself in the project directory:\n$ npm test\n\n2 passed\nwarning: slow\n\n(exit code 0)",
    );
  });

  test("silence, failure and a timeout are each said plainly", () => {
    expect(shellMessageText(shellRecord("true", { stdout: "", stderr: "", exitCode: 0 }))).toContain("(it printed nothing)");
    expect(shellMessageText(shellRecord("false", { stdout: "", stderr: "", exitCode: 1 }))).toContain("(exit code 1)");
    const stopped = shellRecord("sleep 999", { stdout: "", stderr: "", exitCode: 143, timedOut: true });
    expect(stopped.timedOut).toBe(true);
    expect(shellMessageText(stopped)).toContain("(it was stopped: it ran too long)");
  });

  test("very long output is cut in the middle for the conversation", () => {
    const record = shellRecord("cat big", { stdout: `START${"x".repeat(20_000)}END`, stderr: "", exitCode: 0 });

    expect(record.output.length).toBeLessThan(8_200);
    expect(record.output.startsWith("START")).toBe(true);
    expect(record.output.endsWith("END")).toBe(true);
    expect(record.output).toContain("characters left out");
  });
});

describe("isIdle", () => {
  const user = { id: "u", role: "user", parts: [{ type: "text" }] };
  const reply = (...parts: { type: string; state?: string }[]) => ({ id: "a", role: "assistant", parts });

  test("at rest when nothing is being produced and nothing is waiting", () => {
    expect(isIdle([], "ready")).toBe(true);
    expect(isIdle([user, reply({ type: "text" })], "ready")).toBe(true);
    expect(isIdle([user, reply({ type: "tool-bash", state: "output-available" }, { type: "tool-readFile", state: "output-error" })], "ready")).toBe(true);
  });

  test("not while a reply is being produced, even before its first word", () => {
    expect(isIdle([user], "submitted")).toBe(false);
    expect(isIdle([user, reply({ type: "text" })], "streaming")).toBe(false);
    expect(isIdle([user], "error")).toBe(false);
  });

  test("not while a tool call is still to run or waits for the operator's say-so", () => {
    // Between a reply that asked for a tool and the continuation, the status is "ready" — but the turn is not over.
    expect(isIdle([user, reply({ type: "tool-bash", state: "input-available" })], "ready")).toBe(false);
    expect(isIdle([user, reply({ type: "tool-readFile", state: "output-available" }, { type: "dynamic-tool", state: "input-streaming" })], "ready")).toBe(false);
  });
});

describe("withUnsent", () => {
  const messages = [{ id: "u1" }, { id: "a1" }, { id: "s1" }, { id: "s2" }, { id: "u2" }];

  test("a prompt takes along the commands run since the last reply, in order", () => {
    expect(withUnsent(messages, 4, new Set(["s1", "s2"])).map((m) => m.id)).toEqual(["s1", "s2", "u2"]);
  });

  test("only those right before it, and nothing when there are none", () => {
    expect(withUnsent(messages, 4, new Set(["s2"])).map((m) => m.id)).toEqual(["s2", "u2"]);
    expect(withUnsent(messages, 4, new Set(["u1", "s1"])).map((m) => m.id)).toEqual(["u2"]);   // s2 breaks the run
    expect(withUnsent(messages, 4, new Set()).map((m) => m.id)).toEqual(["u2"]);
    expect(withUnsent(messages, 0, new Set(["u1"])).map((m) => m.id)).toEqual(["u1"]);
  });
});
