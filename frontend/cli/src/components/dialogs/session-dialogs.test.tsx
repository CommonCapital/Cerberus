import { afterEach, expect, test } from "bun:test";
import { useEffect, type ReactNode } from "react";
import { testRender } from "@opentui/react/test-utils";
import { createMemoryRouter, RouterProvider, useParams } from "react-router";
import { RootLayout } from "../../layouts/root-layout";
import { useDialog } from "../../providers/dialog";
import { ForkDialogContent, RenameDialogContent, type ForkIo } from "./session-dialogs";

// The dialogs are opened inside the real layout, as a command would open them.
// What they ask of the server is handed in, so nothing is mocked module-wide.

function Opener({ title, children }: { title: string; children: ReactNode }) {
  const { open } = useDialog();
  useEffect(() => {
    open({ title, children });
    // Opened once: the dialog's content is fixed for the test.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);
  return <text>CHAT</text>;
}

function Landed() {
  const { id } = useParams();
  return <text>NOW IN {id}</text>;
}

async function screen(title: string, dialog: ReactNode) {
  const router = createMemoryRouter([
    {
      path: "/",
      element: <RootLayout />,
      children: [
        { index: true, element: <Opener title={title}>{dialog}</Opener> },
        { path: "sessions/:id", element: <Landed /> },
      ],
    },
  ]);
  const rendered = await testRender(<RouterProvider router={router} />, { width: 110, height: 30 });
  const settle = async (ms = 20) => {
    await new Promise((resolve) => setTimeout(resolve, ms));
    await rendered.renderOnce();
  };
  await settle();
  await settle();
  return { ...rendered, settle };
}

let current: Awaited<ReturnType<typeof screen>> | null = null;
afterEach(() => {
  current?.renderer.destroy();
  current = null;
});

const user = (id: string, text: string) => ({ id, role: "user", parts: [{ type: "text", text }] });
const reply = (id: string) => ({ id, role: "assistant", parts: [{ type: "text", text: "ok" }] });
const conversation = [user("u1", "Find the login bug."), reply("a1"), user("u2", "Now fix it."), reply("a2"), user("u3", "Add a test."), reply("a3")];

function forkIo(change: Partial<ForkIo> = {}) {
  const calls: string[] = [];
  const io: ForkIo = {
    load: async (id) => {
      calls.push(`load ${id}`);
      return { messages: conversation };
    },
    fork: async (id, options) => {
      calls.push(`fork ${id} through ${options.through ?? "the end"}`);
      return { id: "fork-1", title: "Login bug (fork)" };
    },
    ...change,
  };
  return { io, calls };
}

test("the fork dialog offers the whole conversation and each earlier reply, latest first", async () => {
  const { io } = forkIo();
  const ui = (current = await screen("Fork this conversation", <ForkDialogContent sessionId="s1" io={io} />));

  const text = ui.captureCharFrame();
  if (process.env.SHOW_FRAMES) console.log(text);
  expect(text).toContain("Fork this conversation");
  expect(text).toContain("The whole conversation");
  expect(text).toContain("6 messages");
  expect(text).toContain("Up to the reply to: Now fix it.");
  expect(text).toContain("Up to the reply to: Find the login bug.");
  expect(text.indexOf("Now fix it.")).toBeLessThan(text.indexOf("Find the login bug."));
  expect(text).not.toContain("Add a test.");   // ending at the last reply is the whole conversation
});

test("enter forks the whole conversation and takes the operator to the copy", async () => {
  const { io, calls } = forkIo();
  const ui = (current = await screen("Fork this conversation", <ForkDialogContent sessionId="s1" io={io} />));

  ui.mockInput.pressEnter();
  await ui.settle();
  await ui.settle();

  expect(calls).toEqual(["load s1", "fork s1 through the end"]);
  const text = ui.captureCharFrame();
  expect(text).toContain("NOW IN fork-1");
  expect(text).toContain("You are now in the copy");
  expect(text).not.toContain("The whole conversation");   // the dialog is closed
});

test("an earlier point is chosen with the arrows", async () => {
  const { io, calls } = forkIo();
  const ui = (current = await screen("Fork this conversation", <ForkDialogContent sessionId="s1" io={io} />));

  ui.mockInput.pressArrow("down");
  ui.mockInput.pressArrow("down");
  await ui.settle();
  ui.mockInput.pressEnter();
  await ui.settle();
  await ui.settle();

  expect(calls.at(-1)).toBe("fork s1 through a1");
});

test("a fork that fails says why and leaves the operator where they were", async () => {
  const { io } = forkIo({
    fork: async () => {
      throw new Error("Session not found");
    },
  });
  const ui = (current = await screen("Fork this conversation", <ForkDialogContent sessionId="s1" io={io} />));

  ui.mockInput.pressEnter();
  await ui.settle();
  await ui.settle();

  const text = ui.captureCharFrame();
  expect(text).toContain("Session not found");
  expect(text).not.toContain("NOW IN");
});

test("the rename dialog names the conversation with what was typed", async () => {
  const calls: string[] = [];
  const rename = async (id: string, title: string) => {
    calls.push(`${id}: ${title}`);
    return { title };
  };
  const ui = (current = await screen("Rename this conversation", <RenameDialogContent sessionId="s1" rename={rename} />));
  expect(ui.captureCharFrame()).toContain("Type the name and press Enter.");

  for (const key of "Token expiry") ui.mockInput.pressKey(key);
  await ui.settle();
  expect(ui.captureCharFrame()).toContain('Rename to "Token expiry"');
  ui.mockInput.pressEnter();
  await ui.settle();
  await ui.settle();

  expect(calls).toEqual(["s1: Token expiry"]);
  const text = ui.captureCharFrame();
  expect(text).toContain('This conversation is now called "Token expiry".');
  expect(text).not.toContain("Rename this conversation");   // closed
});

test("a name the server refuses stays in the dialog with the reason", async () => {
  const rename = async () => {
    throw new Error("A title is one line of at most 100 characters.");
  };
  const ui = (current = await screen("Rename this conversation", <RenameDialogContent sessionId="s1" rename={rename} />));

  for (const key of "x") ui.mockInput.pressKey(key);
  await ui.settle();
  ui.mockInput.pressEnter();
  await ui.settle();
  await ui.settle();

  const text = ui.captureCharFrame();
  expect(text).toContain("A title is one line of at most 100 characters.");
  expect(text).toContain("Rename this conversation");
});
