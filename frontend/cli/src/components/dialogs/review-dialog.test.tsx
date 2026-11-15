import { afterEach, beforeEach, expect, mock, test } from "bun:test";
import { useEffect } from "react";
import { testRender } from "@opentui/react/test-utils";
import { createMemoryRouter, RouterProvider } from "react-router";

const submitted: unknown[] = [];

mock.module("../../lib/cerberus/api", () => ({
  cerberus: {
    submitJob: async (job: unknown) => {
      submitted.push(job);
      return { id: "job-5" };
    },
  },
}));

const { ReviewDialogContent } = await import("./review-dialog");
const { RootLayout } = await import("../../layouts/root-layout");
const { useDialog } = await import("../../providers/dialog");

function Opener() {
  const { open } = useDialog();
  useEffect(() => {
    open({ title: "Security review", children: <ReviewDialogContent model="llama3.1" /> });
    // Opened once, as the command would.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  return <text>HOME</text>;
}

async function screen() {
  const router = createMemoryRouter([{
    path: "/",
    element: <RootLayout />,
    children: [
      { index: true, element: <Opener /> },
      { path: "runs/:id", element: <text>RUN SCREEN</text> },
    ],
  }]);
  const rendered = await testRender(<RouterProvider router={router} />, { width: 120, height: 30 });
  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
    await rendered.renderOnce();
  };
  await settle();
  await settle();
  return { ...rendered, settle };
}

let current: Awaited<ReturnType<typeof screen>> | null = null;
beforeEach(() => {
  submitted.length = 0;
});
afterEach(() => {
  current?.renderer.destroy();
  current = null;
});

test("enter plans a rules-only review of the whole scan root and opens its run", async () => {
  const ui = (current = await screen());
  expect(ui.captureCharFrame()).toContain("Rules only");
  expect(ui.captureCharFrame()).toContain("With llama3.1");

  ui.mockInput.pressEnter();
  await ui.settle();

  expect(submitted).toEqual([{ type: "review", path: "." }]);
  expect(ui.captureCharFrame()).toContain("RUN SCREEN");
});

test("a typed path with the model chosen plans a review that dispatches every agent", async () => {
  const ui = (current = await screen());

  for (const key of "svc") ui.mockInput.pressKey(key);
  await ui.settle();
  expect(ui.captureCharFrame()).toContain('"svc" — with llama3.1');
  ui.mockInput.pressArrow("down");
  await ui.settle();
  ui.mockInput.pressEnter();
  await ui.settle();

  expect(submitted).toEqual([
    { type: "review", path: "svc", agentModel: "llama3.1", agents: ["sast", "threat-model", "evidence"] },
  ]);
});
