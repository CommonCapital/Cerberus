import { afterEach, expect, test } from "bun:test";
import { testRender } from "@opentui/react/test-utils";
import { KeyboardLayerProvider } from "../keyboard-layer";
import { ThemeProvider } from "../theme";
import { ToolPermissionPrompt, ToolPermissionProvider, useToolPermission, type ToolDecision, type ToolRequest } from ".";

let ask: (request: ToolRequest) => Promise<ToolDecision> = async () => "deny";
let denyAll: () => void = () => {};

function Capture() {
  const permission = useToolPermission();
  ask = permission.ask;
  denyAll = permission.denyAll;
  return null;
}

async function screen() {
  const rendered = await testRender(
    <ThemeProvider>
      <KeyboardLayerProvider>
        <ToolPermissionProvider>
          <Capture />
          <text>CHAT</text>
          <ToolPermissionPrompt />
        </ToolPermissionProvider>
      </KeyboardLayerProvider>
    </ThemeProvider>,
    { width: 100, height: 20 },
  );
  const settle = async (ms = 20) => {
    await new Promise((resolve) => setTimeout(resolve, ms));
    await rendered.renderOnce();
  };
  await settle();
  return { ...rendered, settle };
}

let current: Awaited<ReturnType<typeof screen>> | null = null;
afterEach(() => {
  current?.renderer.destroy();
  current = null;
});

const bash = (command: string): ToolRequest => ({ toolName: "bash", input: { command } });

test("nothing is shown until a tool call needs a decision", async () => {
  const ui = (current = await screen());

  expect(ui.captureCharFrame()).toContain("CHAT");
  expect(ui.captureCharFrame()).not.toContain("Allow this?");
});

test("the prompt shows what will run, and y allows it once", async () => {
  const ui = (current = await screen());
  const decision = ask(bash("npm test"));
  await ui.settle();

  const frame = ui.captureCharFrame();
  if (process.env.SHOW_FRAMES) console.log(frame);
  expect(frame).toContain("Allow this?");
  expect(frame).toContain("Run a shell command");
  expect(frame).toContain("npm test");
  expect(frame).toContain("y yes, once a always allow `npm test` n no");   // a rule for this command, not for every command

  ui.mockInput.pressKey("y");
  await ui.settle();

  expect(await decision).toBe("allow");
  expect(ui.captureCharFrame()).not.toContain("Allow this?");
});

test("n denies, and so does escape", async () => {
  const ui = (current = await screen());

  const first = ask(bash("rm -rf /"));
  await ui.settle();
  ui.mockInput.pressKey("n");
  await ui.settle();
  expect(await first).toBe("deny");

  const second = ask({ toolName: "writeFile", input: { path: "a.txt", content: "x" } });
  await ui.settle();
  expect(ui.captureCharFrame()).toContain("Write a.txt");
  ui.mockInput.pressEscape();
  await ui.settle(250);   // a lone Escape is held back briefly by the input parser
  expect(await second).toBe("deny");
});

test("requests are answered one at a time, in order", async () => {
  const ui = (current = await screen());
  const first = ask(bash("first"));
  const second = ask({ toolName: "editFile", input: { path: "b.py", oldString: "a", newString: "b" } });
  await ui.settle();

  expect(ui.captureCharFrame()).toContain("first");
  expect(ui.captureCharFrame()).toContain("1 more waiting");

  ui.mockInput.pressKey("y");
  await ui.settle();
  expect(await first).toBe("allow");
  expect(ui.captureCharFrame()).toContain("Edit b.py");

  ui.mockInput.pressKey("n");
  await ui.settle();
  expect(await second).toBe("deny");
});

test("always for an edit also answers the waiting edits, and only those", async () => {
  const ui = (current = await screen());
  const first = ask({ toolName: "editFile", input: { path: "a.txt", oldString: "x", newString: "y" } });
  const sameKind = ask({ toolName: "writeFile", input: { path: "b.txt", content: "" } });
  const otherKind = ask(bash("npm test"));
  await ui.settle();
  expect(ui.captureCharFrame()).toContain("always allow editing files");

  ui.mockInput.pressKey("a");
  await ui.settle();

  expect(await first).toBe("always");
  expect(await sameKind).toBe("allow");
  expect(ui.captureCharFrame()).toContain("npm test");   // still asked: a different kind of action

  ui.mockInput.pressKey("n");
  await ui.settle();
  expect(await otherKind).toBe("deny");
});

test("always for a command covers that command, not every command", async () => {
  const ui = (current = await screen());
  const first = ask(bash("git status"));
  const covered = ask(bash("git status --short"));
  const other = ask(bash("git push --force"));
  await ui.settle();
  expect(ui.captureCharFrame()).toContain("always allow `git status`");

  ui.mockInput.pressKey("a");
  await ui.settle();

  expect(await first).toBe("always");
  expect(await covered).toBe("allow");
  expect(ui.captureCharFrame()).toContain("git push --force");   // a different command: still asked
  ui.mockInput.pressKey("n");
  await ui.settle();
  expect(await other).toBe("deny");
});

test("a command line that must be looked at every time has no always", async () => {
  const ui = (current = await screen());
  const answers: ToolDecision[] = [];
  void ask(bash("rm -rf build && npm test")).then((d) => { answers.push(d); });
  await ui.settle();
  expect(ui.captureCharFrame()).not.toContain("always allow");

  ui.mockInput.pressKey("a");   // not an answer here
  await ui.settle();
  expect(answers).toEqual([]);
  ui.mockInput.pressKey("y");
  await ui.settle();
  expect(answers).toEqual(["allow"]);
});

test("leaving denies whatever is still waiting", async () => {
  const ui = (current = await screen());
  const first = ask(bash("one"));
  const second = ask(bash("two"));
  await ui.settle();

  denyAll();
  await ui.settle();

  expect(await first).toBe("deny");
  expect(await second).toBe("deny");
  expect(ui.captureCharFrame()).not.toContain("Allow this?");
});

test("t denies with the operator's own words for the model; typing them triggers no other key", async () => {
  const ui = (current = await screen());
  const decision = ask(bash("rm -rf build"));
  await ui.settle();
  expect(ui.captureCharFrame()).toContain("t no, and tell it what to do instead");

  ui.mockInput.pressKey("t");
  await ui.settle();
  expect(ui.captureCharFrame()).toContain("Not this — what instead?");

  // "y", "a" and "n" are answers on the first screen; here they are just letters.
  for (const key of "any way but that") ui.mockInput.pressKey(key);
  await ui.settle();
  ui.mockInput.pressEnter();
  await ui.settle();

  expect(await decision).toEqual({ deny: "any way but that" });
  expect(ui.captureCharFrame()).not.toContain("Not this");
});

test("escape in the explanation goes back to the choices without deciding", async () => {
  const ui = (current = await screen());
  const answers: ToolDecision[] = [];
  void ask(bash("make deploy")).then((d) => { answers.push(d); });
  await ui.settle();

  ui.mockInput.pressKey("t");
  await ui.settle();
  ui.mockInput.pressEscape();
  await ui.settle(250);

  expect(answers).toEqual([]);
  expect(ui.captureCharFrame()).toContain("Allow this?");
  ui.mockInput.pressKey("y");
  await ui.settle();
  expect(answers).toEqual(["allow"]);
});

test("reading a file that holds secrets is asked about by name, and cannot be always allowed", async () => {
  const ui = (current = await screen());
  const answers: ToolDecision[] = [];
  void ask({ toolName: "readFile", input: { path: "config/.env.production" } }).then((d) => { answers.push(d); });
  await ui.settle();

  const text = ui.captureCharFrame();
  expect(text).toContain("Read config/.env.production");
  expect(text).toContain("probably holds secrets");
  expect(text).not.toContain("always allow");

  ui.mockInput.pressKey("a");   // not an answer here
  await ui.settle();
  expect(answers).toEqual([]);
  ui.mockInput.pressKey("y");
  await ui.settle();
  expect(answers).toEqual(["allow"]);
});
