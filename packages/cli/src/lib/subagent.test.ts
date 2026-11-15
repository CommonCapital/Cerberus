import { expect, test } from "bun:test";
import { Chat } from "@ai-sdk/react";
import { lastAssistantMessageIsCompleteWithToolCalls, type ChatTransport, type UIMessage, type UIMessageChunk } from "ai";
import { MAX_ROUNDS, pendingCalls, reportOf, runHelper, settleInterruptedHelpers, type HelperDeps } from "./subagent";
import { HelperReply, MAX_HELPERS_PER_REPLY } from "./helper-budget";
import { isIdle } from "./chat-flow";

// A scripted model: each time the helper's conversation is sent, the next reply of the script comes back.
type Reply = UIMessageChunk[] | Error;

const callTool = (id: string, toolName: string, input: unknown): UIMessageChunk[] => [
  { type: "start", messageId: "a1" },
  { type: "start-step" },
  { type: "tool-input-available", toolCallId: id, toolName, input },
  { type: "finish-step" },
  { type: "finish" },
];
const say = (text: string, before: UIMessageChunk[] = []): UIMessageChunk[] => [
  { type: "start", messageId: "a1" },
  { type: "start-step" },
  ...before,
  { type: "text-start", id: `t${text.length}` },
  { type: "text-delta", id: `t${text.length}`, delta: text },
  { type: "text-end", id: `t${text.length}` },
  { type: "finish-step" },
  { type: "finish" },
];

function harness(script: Reply[], change: Partial<HelperDeps> = {}) {
  const seen = {
    sent: [] as UIMessage[][],
    sessions: [] as { title: string; parentId: string }[],
    tools: [] as { toolName: string; input: unknown }[],
    progress: [] as string[],
    model: "",
  };
  const transport: ChatTransport<UIMessage> = {
    sendMessages: async ({ messages }) => {
      seen.sent.push(structuredClone(messages));
      const reply = script.shift();
      if (!reply) throw new Error("the script has no more replies");
      if (reply instanceof Error) throw reply;
      return new ReadableStream<UIMessageChunk>({
        start(controller) {
          for (const chunk of reply) controller.enqueue(chunk);
          controller.close();
        },
      });
    },
    reconnectToStream: async () => null,
  };
  const deps: HelperDeps = {
    createSession: async (title, parentId) => {
      seen.sessions.push({ title, parentId });
      return "child-1";
    },
    transport: (_sessionId, model) => {
      seen.model = model;
      return transport;
    },
    runTool: async (toolName, input) => {
      seen.tools.push({ toolName, input });
      return { content: `contents of ${(input as { path?: string }).path ?? "?"}` };
    },
    onProgress: (text) => void seen.progress.push(text),
    ...change,
  };
  return { deps, seen };
}

const task = { parentSessionId: "parent-1", description: "Find the loader", prompt: "Where is pickle used? Give file and line.", model: "llama3.1" };

test("a helper works in its own conversation and its last message is the report", async () => {
  const { deps, seen } = harness([
    callTool("c1", "grep", { pattern: "pickle" }),
    callTool("c2", "readFile", { path: "svc/loader.py" }),
    say("svc/loader.py:7 calls pickle.loads on request data."),
  ]);

  const output = await runHelper(task, deps);

  expect(output).toEqual({ report: "svc/loader.py:7 calls pickle.loads on request data.", toolCalls: 2, complete: true });
  // Its own conversation, under the chat that started it, with the same model.
  expect(seen.sessions).toEqual([{ title: "Find the loader", parentId: "parent-1" }]);
  expect(seen.model).toBe("llama3.1");
  // It starts from the prompt alone: nothing of the parent conversation.
  expect(seen.sent[0]).toHaveLength(1);
  expect(seen.sent[0]![0]).toMatchObject({ role: "user", parts: [{ type: "text", text: task.prompt }] });
  // Each tool call was run here and its result went back with the same reply.
  expect(seen.tools).toEqual([
    { toolName: "grep", input: { pattern: "pickle" } },
    { toolName: "readFile", input: { path: "svc/loader.py" } },
  ]);
  const second = seen.sent[1]!.at(-1)!;
  expect(second.role).toBe("assistant");
  expect(second.parts.find((part) => part.type === "tool-grep")).toMatchObject({ state: "output-available", output: { content: "contents of ?" } });
  expect(seen.progress).toEqual(["starting", "1 tool call · grep pickle", "2 tool calls · readFile svc/loader.py"]);
});

test("a tool that fails or is refused is told to the helper, which carries on", async () => {
  const { deps, seen } = harness([callTool("c1", "readFile", { path: ".env" }), say("I could not read .env; nothing else to report.")], {
    runTool: async () => {
      throw new Error("The operator denied this tool call.");
    },
  });

  const output = await runHelper(task, deps);

  expect(output.complete).toBe(true);
  expect(output.report).toBe("I could not read .env; nothing else to report.");
  const part = seen.sent[1]!.at(-1)!.parts.find((p) => p.type === "tool-readFile");
  expect(part).toMatchObject({ state: "output-error", errorText: "The operator denied this tool call." });
});

test("several calls in one step are all run before the model is asked again", async () => {
  const { deps, seen } = harness([
    [
      { type: "start", messageId: "a1" },
      { type: "start-step" },
      { type: "tool-input-available", toolCallId: "c1", toolName: "readFile", input: { path: "a.py" } },
      { type: "tool-input-available", toolCallId: "c2", toolName: "readFile", input: { path: "b.py" } },
      { type: "finish-step" },
      { type: "finish" },
    ],
    say("Both read."),
  ]);

  const output = await runHelper(task, deps);

  expect(output).toMatchObject({ toolCalls: 2, complete: true });
  expect(seen.sent).toHaveLength(2);
});

test("a tool the server ran itself is not run again here", async () => {
  const { deps, seen } = harness([
    say("Done after looking in the skills library.", [
      { type: "tool-input-available", toolCallId: "s1", toolName: "searchSkills", input: { query: "pickle" }, providerExecuted: true },
      { type: "tool-output-available", toolCallId: "s1", output: { results: [] }, providerExecuted: true },
    ]),
  ]);

  const output = await runHelper(task, deps);

  expect(output).toMatchObject({ toolCalls: 0, complete: true });
  expect(seen.tools).toEqual([]);
});

test("a helper that never stops calling tools is stopped, and says so", async () => {
  const script = Array.from({ length: MAX_ROUNDS + 1 }, (_, i) => callTool(`c${i}`, "readFile", { path: `f${i}.py` }));
  const { deps, seen } = harness(script);

  const output = await runHelper(task, deps);

  expect(output.complete).toBe(false);
  expect(output.toolCalls).toBe(MAX_ROUNDS);
  expect(output.note).toContain(`stopped after ${MAX_ROUNDS} rounds`);
  expect(seen.sent).toHaveLength(MAX_ROUNDS + 1);
});

test("a helper that ends without saying anything has no report, and says so", async () => {
  const { deps } = harness([[{ type: "start", messageId: "a1" }, { type: "start-step" }, { type: "finish-step" }, { type: "finish" }]]);

  expect(await runHelper(task, deps)).toEqual({ report: "", toolCalls: 0, complete: false, note: "The helper ended without writing a report." });
});

test("a model that cannot be reached fails the task with the reason", async () => {
  const { deps } = harness([new Error("Cannot reach the model")]);

  await expect(runHelper(task, deps)).rejects.toThrow("The helper failed: Cannot reach the model");
});

test("an interrupted helper stops and has no report", async () => {
  const controller = new AbortController();
  const { deps, seen } = harness([callTool("c1", "readFile", { path: "a.py" }), say("never reached")], {
    runTool: async () => {
      controller.abort();   // the operator presses Esc while the helper is working
      return { content: "x" };
    },
  });

  await expect(runHelper({ ...task, signal: controller.signal }, deps)).rejects.toThrow("interrupted");
  expect(seen.sent).toHaveLength(1);   // no continuation is sent after the interrupted read

  const already = new AbortController();
  already.abort();
  const second = harness([say("never")]);
  await expect(runHelper({ ...task, signal: already.signal }, second.deps)).rejects.toThrow("interrupted");
  expect(second.seen.sessions).toEqual([]);   // nothing was started
});

test("stopping while the child session is being created never starts a model request", async () => {
  const controller = new AbortController();
  let finishSession!: (id: string) => void;
  const creating = new Promise<string>((resolve) => { finishSession = resolve; });
  const { deps, seen } = harness([say("never requested")], { createSession: () => creating });
  const pending = runHelper({ ...task, signal: controller.signal }, deps);
  controller.abort();
  finishSession("late-child");
  await expect(pending).rejects.toThrow("interrupted");
  expect(seen.model).toBe("");   // not even a transport is created
  expect(seen.sent).toEqual([]);
  expect(seen.progress).toEqual([]);
});

test("stopping from the initial progress callback does not send the prompt", async () => {
  const controller = new AbortController();
  const { deps, seen } = harness([say("never requested")], { onProgress: () => controller.abort() });
  await expect(runHelper({ ...task, signal: controller.signal }, deps)).rejects.toThrow("interrupted");
  expect(seen.sent).toEqual([]);
});

test("the per-reply budget caps real helper session creation even when every model request fails", async () => {
  const budget = new HelperReply<Awaited<ReturnType<typeof runHelper>>>();
  const { deps, seen } = harness(Array.from({ length: MAX_HELPERS_PER_REPLY }, () => new Error("offline")));
  for (let i = 0; i < MAX_HELPERS_PER_REPLY; i++) {
    await expect(budget.request(`task-${i}`, (signal) => runHelper({ ...task, signal }, deps)).result).rejects.toThrow("offline");
  }
  await expect(budget.request("one-more", (signal) => runHelper({ ...task, signal }, deps)).result).rejects.toThrow("limit of 4 helpers");
  expect(seen.sessions).toHaveLength(MAX_HELPERS_PER_REPLY);
  expect(seen.sent).toHaveLength(MAX_HELPERS_PER_REPLY);
});

test("a replay of a task call reuses its report without creating another child", async () => {
  const budget = new HelperReply<Awaited<ReturnType<typeof runHelper>>>();
  const { deps, seen } = harness([say("one report")]);
  const request = () => budget.request("task-1", (signal) => runHelper({ ...task, signal }, deps));
  const first = request();
  const replay = request();
  expect(replay.first).toBe(false);
  expect(await first.result).toEqual(await replay.result);
  expect((await request().result).report).toBe("one report");
  expect(seen.sessions).toHaveLength(1);
  expect(seen.sent).toHaveLength(1);
});

test("stopping a parent task settles it without another model call, and the next prompt works", async () => {
  let reply = new HelperReply<string>();
  let finishHelper!: (report: string) => void;
  const slowHelper = new Promise<string>((resolve) => { finishHelper = resolve; });
  const { deps, seen } = harness([callTool("task-1", "task", { prompt: "look around" }), say("The new prompt was answered.")]);
  let helperResult: Promise<string> | undefined;
  const chat = new Chat<UIMessage>({
    transport: deps.transport("parent", "echo"),
    onToolCall({ toolCall }) {
      helperResult = reply.request(toolCall.toolCallId, () => slowHelper).result;
      void helperResult.catch(() => {});
    },
    sendAutomaticallyWhen: (options) => !reply.signal.aborted && lastAssistantMessageIsCompleteWithToolCalls(options),
  });

  await chat.sendMessage({ text: "First prompt" });
  expect(isIdle(chat.messages, chat.status)).toBe(false);
  reply.cancel();
  await chat.stop();
  await settleInterruptedHelpers(chat.lastMessage, (toolCallId, errorText) =>
    chat.addToolOutput({ tool: "task", toolCallId, state: "output-error", errorText }));
  await expect(helperResult!).rejects.toThrow("interrupted");
  expect(isIdle(chat.messages, chat.status)).toBe(true);
  expect(chat.lastMessage?.parts.find((part) => part.type === "tool-task")).toMatchObject({ state: "output-error" });
  expect(seen.sent).toHaveLength(1);   // settling the tool never resumes the stopped model

  reply = new HelperReply<string>();   // what submit does, not a tool continuation
  await chat.sendMessage({ text: "Next prompt" });
  expect(reportOf(chat.lastMessage)).toBe("The new prompt was answered.");
  expect(isIdle(chat.messages, chat.status)).toBe(true);
  expect(seen.sent).toHaveLength(2);
  expect(await reply.request("task-1", async () => "new allowance").result).toBe("new allowance");
  finishHelper("a late old report");
  await Promise.resolve();
  expect(reportOf(chat.lastMessage)).toBe("The new prompt was answered.");
});

test("settling a task after the parent fails preserves the main error and does not resume", async () => {
  const reply = new HelperReply<string>();
  const { deps, seen } = harness([[
    ...callTool("task-1", "task", { prompt: "look around" }),
    { type: "error", errorText: "The main model failed." },
  ]]);
  let settling = Promise.resolve();
  const chat = new Chat<UIMessage>({
    transport: deps.transport("parent", "echo"),
    onError() {
      reply.cancel();
      settling = settleInterruptedHelpers(chat.lastMessage, (toolCallId, errorText) =>
        chat.addToolOutput({ tool: "task", toolCallId, state: "output-error", errorText }));
    },
    sendAutomaticallyWhen: (options) => !reply.signal.aborted && lastAssistantMessageIsCompleteWithToolCalls(options),
  });
  await chat.sendMessage({ text: "First prompt" });
  await settling;
  expect(chat.status).toBe("error");
  expect(chat.error?.message).toBe("The main model failed.");
  expect(chat.lastMessage?.parts.find((part) => part.type === "tool-task")).toMatchObject({ state: "output-error" });
  expect(seen.sent).toHaveLength(1);
});

test("stopping during a task's incomplete JSON settles it and permits the next prompt", async () => {
  let reply = new HelperReply<string>();
  let sawPartial!: () => void;
  const partialSeen = new Promise<void>((resolve) => { sawPartial = resolve; });
  let requests = 0;
  let helperCalls = 0;
  const transport: ChatTransport<UIMessage> = {
    sendMessages: async ({ abortSignal }) => {
      requests++;
      if (requests > 1) {
        return new ReadableStream<UIMessageChunk>({
          start(controller) {
            for (const chunk of say("The next prompt works.")) controller.enqueue(chunk);
            controller.close();
          },
        });
      }
      return new ReadableStream<UIMessageChunk>({
        start(controller) {
          controller.enqueue({ type: "start", messageId: "partial-task" });
          controller.enqueue({ type: "start-step" });
          controller.enqueue({ type: "tool-input-start", toolCallId: "task-half", toolName: "task" });
          controller.enqueue({ type: "tool-input-delta", toolCallId: "task-half", inputTextDelta: '{"prompt":"not finished' });
          controller.enqueue({ type: "data-partial-ready", data: true, transient: true });
          abortSignal?.addEventListener("abort", () => controller.close(), { once: true });
        },
      });
    },
    reconnectToStream: async () => null,
  };
  const chat = new Chat<UIMessage>({
    transport,
    onData() { sawPartial(); },
    onToolCall() { helperCalls++; },
    sendAutomaticallyWhen: (options) => !reply.signal.aborted && lastAssistantMessageIsCompleteWithToolCalls(options),
  });
  const first = chat.sendMessage({ text: "Start a helper" });
  await partialSeen;
  expect(chat.lastMessage?.parts.find((part) => part.type === "tool-task")).toMatchObject({ state: "input-streaming" });
  expect(helperCalls).toBe(0);
  reply.cancel();
  await chat.stop();
  await first;
  await settleInterruptedHelpers(chat.lastMessage, (toolCallId, errorText) =>
    chat.addToolOutput({ tool: "task", toolCallId, state: "output-error", errorText }));
  expect(isIdle(chat.messages, chat.status)).toBe(true);
  expect(requests).toBe(1);
  reply = new HelperReply<string>();
  await chat.sendMessage({ text: "Try again" });
  expect(reportOf(chat.lastMessage)).toBe("The next prompt works.");
  expect(isIdle(chat.messages, chat.status)).toBe(true);
  expect(requests).toBe(2);
});

test("a task without a prompt is refused before anything is started", async () => {
  const { deps, seen } = harness([]);

  await expect(runHelper({ ...task, prompt: "  " }, deps)).rejects.toThrow("no prompt");
  expect(seen.sessions).toEqual([]);
});

test("a description is one short line; a very long report is cut", async () => {
  const long = "x".repeat(30_000);
  const { deps, seen } = harness([say(long)]);

  const output = await runHelper({ ...task, description: `  Find\n the   loader ${"y".repeat(200)}` }, deps);

  expect(seen.sessions[0]!.title).toHaveLength(80);
  expect(seen.sessions[0]!.title.startsWith("Find the loader y")).toBe(true);
  expect(output.report.length).toBeLessThan(21_000);
  expect(output.report).toContain("the report was cut here");
});

test("the report is the last step's text, not what was said along the way", () => {
  const message = {
    id: "a", role: "assistant",
    parts: [
      { type: "step-start" }, { type: "text", text: "Let me look." }, { type: "tool-readFile", toolCallId: "c", state: "output-available", input: {}, output: {} },
      { type: "step-start" }, { type: "text", text: "The loader is in svc/loader.py." },
    ],
  } as unknown as UIMessage;

  expect(reportOf(message)).toBe("The loader is in svc/loader.py.");
  expect(pendingCalls(message)).toEqual([]);
  expect(reportOf(undefined)).toBe("");
});
