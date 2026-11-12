import { expect, test } from "bun:test";
import { onUndoRequest, requestUndo } from "./undo-events";

test("a request reaches the conversation that is open, and only that one", () => {
  const heard: string[] = [];
  const stopA = onUndoRequest("session-a", (kind) => heard.push(`a:${kind}`));
  const stopB = onUndoRequest("session-b", (kind) => heard.push(`b:${kind}`));

  expect(requestUndo("session-a", "undo")).toBe(true);
  expect(requestUndo("session-b", "redo")).toBe(true);
  expect(heard).toEqual(["a:undo", "b:redo"]);

  stopA();
  stopB();
});

test("with no conversation open on it, the request is not taken", () => {
  expect(requestUndo("nobody-home", "undo")).toBe(false);

  const stop = onUndoRequest("session-c", () => {});
  stop();
  expect(requestUndo("session-c", "undo")).toBe(false);   // the screen was left
});
