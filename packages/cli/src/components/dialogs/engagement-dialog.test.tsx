import { afterAll, afterEach, beforeEach, expect, mock, test } from "bun:test";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { useEffect, type ReactNode } from "react";
import { testRender } from "@opentui/react/test-utils";
import { createMemoryRouter, RouterProvider } from "react-router";
import type { Engagement, EngagementTarget } from "../../lib/cerberus/api";

// Never the operator's real settings directory.
const home = mkdtempSync(join(tmpdir(), "cerberus-test-"));
process.env.CERBERUS_HOME = home;
afterAll(() => rmSync(home, { recursive: true, force: true }));

let engagements: Engagement[] = [];
const calls: string[] = [];

const target = (id: string, locator: string): EngagementTarget => ({ id, type: "Path", locator, verification: "Attested" });
const engagement = (id: string, name: string, extra: Partial<Engagement> = {}): Engagement => ({
  id, name, status: "Active", maxTier: "T0", targets: [], createdAt: "2026-11-12T10:00:00Z", ...extra,
});

mock.module("../../lib/cerberus/api", () => ({
  cerberus: {
    engagements: async () => engagements,
    engagement: async (id: string) => engagements.find((e) => e.id === id)!,
    createEngagement: async (name: string) => {
      calls.push(`create ${name}`);
      const created = engagement(`e-${engagements.length + 1}`, name);
      engagements.push(created);
      return created;
    },
    addPathTarget: async (id: string, path: string) => {
      calls.push(`add ${id} ${path}`);
      if (path === "missing") throw new Error("locator does not exist under the scan root");
      const added = target(`t-${path}`, path);
      engagements.find((e) => e.id === id)!.targets.push(added);
      return added;
    },
    removeTarget: async (id: string, targetId: string) => {
      calls.push(`remove ${id} ${targetId}`);
      const owner = engagements.find((e) => e.id === id)!;
      owner.targets = owner.targets.filter((t) => t.id !== targetId);
      return null;
    },
    abortEngagement: async (id: string) => { calls.push(`abort ${id}`); },
  },
}));

const { AbortDialogContent, EngagementDialogContent, ScopeDialogContent } = await import("./engagement-dialog");
const { getCurrentEngagement, setCurrentEngagement } = await import("../../lib/cerberus/engagement");
const { RootLayout } = await import("../../layouts/root-layout");
const { useDialog } = await import("../../providers/dialog");

function Opener({ title, children }: { title: string; children: ReactNode }) {
  const { open } = useDialog();
  useEffect(() => {
    open({ title, children });
    // Opened once, as a command would.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  return <text>HOME</text>;
}

async function screen(title: string, content: ReactNode) {
  const router = createMemoryRouter([{
    path: "/", element: <RootLayout />, children: [{ index: true, element: <Opener title={title}>{content}</Opener> }],
  }]);
  const rendered = await testRender(<RouterProvider router={router} />, { width: 120, height: 30 });
  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
    await rendered.renderOnce();
  };
  await settle();
  await settle();
  return { ...rendered, settle, type: (text: string) => { for (const key of text) rendered.mockInput.pressKey(key); } };
}

let current: Awaited<ReturnType<typeof screen>> | null = null;
beforeEach(() => {
  process.env.CERBERUS_HOME = home;
  calls.length = 0;
  setCurrentEngagement(null);
  engagements = [
    engagement("e-1", "Payments audit", { targets: [target("t-1", "services/pay")] }),
    engagement("e-2", "Old one", { status: "Closed" }),
  ];
});
afterEach(() => {
  current?.renderer.destroy();
  current = null;
});

test("active engagements can be chosen; ended ones are not offered", async () => {
  const ui = (current = await screen("Engagement", <EngagementDialogContent />));
  const text = ui.captureCharFrame();
  expect(text).toContain("Payments audit");
  expect(text).toContain("1 target in scope");
  expect(text).toContain("✔ No engagement");
  expect(text).not.toContain("Old one");

  ui.mockInput.pressEnter();   // the first row: the active engagement
  await ui.settle();

  expect(getCurrentEngagement()).toEqual({ id: "e-1", name: "Payments audit" });
  expect(ui.captureCharFrame()).toContain('Working under "Payments audit"');
});

test("typing a name creates an engagement and makes it the current one", async () => {
  const ui = (current = await screen("Engagement", <EngagementDialogContent />));

  ui.type("api review");
  await ui.settle();
  expect(ui.captureCharFrame()).toContain('Create engagement "api review"');
  ui.mockInput.pressEnter();
  await ui.settle();

  expect(calls).toEqual(["create api review"]);
  expect(getCurrentEngagement()).toEqual({ id: "e-3", name: "api review" });
  expect(ui.captureCharFrame()).toContain('Created "api review".');
});

test("the scope dialog needs an engagement, then lists, adds and removes directories", async () => {
  const none = (current = await screen("Scope", <ScopeDialogContent />));
  expect(none.captureCharFrame()).toContain("Choose or create an engagement first");
  none.renderer.destroy();

  setCurrentEngagement({ id: "e-1", name: "Payments audit" });
  const ui = (current = await screen("Scope", <ScopeDialogContent />));
  expect(ui.captureCharFrame()).toContain("services/pay");
  expect(ui.captureCharFrame()).toContain("path · attested");

  ui.type("services/web");
  await ui.settle();
  expect(ui.captureCharFrame()).toContain("I may test this code");   // adding is the operator's attestation
  ui.mockInput.pressEnter();
  await ui.settle();
  expect(calls).toEqual(["add e-1 services/web"]);
  expect(ui.captureCharFrame()).toContain("services/web");
  expect(ui.captureCharFrame()).toContain("services/pay");

  ui.mockInput.pressEnter();   // nothing typed: removes the highlighted (first) target
  await ui.settle();
  expect(calls).toEqual(["add e-1 services/web", "remove e-1 t-1"]);
});

test("a path the server refuses is reported and stays in the field", async () => {
  setCurrentEngagement({ id: "e-1", name: "Payments audit" });
  const ui = (current = await screen("Scope", <ScopeDialogContent />));

  ui.type("missing");
  await ui.settle();
  ui.mockInput.pressEnter();
  await ui.settle();

  expect(ui.captureCharFrame()).toContain("locator does not exist under the scan root");
  expect(ui.captureCharFrame()).toContain('Add "missing" to the scope');
});

test("abort asks once, then stops the engagement and leaves it", async () => {
  setCurrentEngagement({ id: "e-1", name: "Payments audit" });
  const ui = (current = await screen("Abort engagement", <AbortDialogContent />));
  expect(ui.captureCharFrame()).toContain("Stop every queued and running job under");
  expect(ui.captureCharFrame()).toContain("enter abort it");
  expect(calls).toEqual([]);

  ui.mockInput.pressEnter();
  await ui.settle();

  expect(calls).toEqual(["abort e-1"]);
  expect(getCurrentEngagement()).toBeNull();
});
