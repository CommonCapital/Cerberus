import { afterEach, expect, test } from "bun:test";
import { useEffect, type ReactNode } from "react";
import { testRender } from "@opentui/react/test-utils";
import { createMemoryRouter, RouterProvider } from "react-router";
import { RootLayout } from "../../layouts/root-layout";
import { useDialog } from "../../providers/dialog";
import type { ScheduleInfo } from "../../lib/cerberus/api";
import { describeSchedule, SchedulesDialogContent, type SchedulesIo } from "./schedules-dialog";

function Opener({ children }: { children: ReactNode }) {
  const { open } = useDialog();
  useEffect(() => {
    open({ title: "Schedules", children });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);
  return <text>HOME</text>;
}

async function screen(io: SchedulesIo) {
  const router = createMemoryRouter([
    { path: "/", element: <RootLayout />, children: [{ index: true, element: <Opener><SchedulesDialogContent io={io} /></Opener> }] },
  ]);
  const rendered = await testRender(<RouterProvider router={router} />, { width: 130, height: 30 });
  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
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

const schedule = (id: string, extra: Partial<ScheduleInfo> = {}): ScheduleInfo => ({
  id, name: `scan ${id}`, type: "scan", targetPath: ".", everyMinutes: 1440, every: "every day", enabled: true,
  nextRunAt: "2026-11-13T02:00:00Z", lastRunAt: null, lastOutcome: null, lastJobId: null, ...extra,
});

test("a schedule's row says what matters about it now", () => {
  expect(describeSchedule(schedule("a"))).toBe("every day · next 11-13 02:00");
  expect(describeSchedule(schedule("a", { lastOutcome: "Started." }))).toBe("every day · next 11-13 02:00");
  expect(describeSchedule(schedule("a", { lastOutcome: 'Waiting for your approval: Run a scan of ".".' }))).toBe("waiting for your approval");
  expect(describeSchedule(schedule("a", { lastOutcome: "Refused: Unknown catalog 'x'" }))).toBe("last run was refused");
  expect(describeSchedule(schedule("a", { enabled: false, lastOutcome: "Started." }))).toBe("paused");
  expect(describeSchedule(schedule("a", { enabled: false, lastOutcome: "Paused after 3 refusals in a row. Last: Refused: x" }))).toBe("paused: it kept being refused");
});

test("the dialog lists the schedules, and enter pauses the highlighted one", async () => {
  let stored = [schedule("one", { name: "Nightly scan", lastOutcome: "Started." }), schedule("two", { name: "Weekly review", every: "every week" })];
  const calls: string[] = [];
  const io: SchedulesIo = {
    list: async () => stored,
    setEnabled: async (id, enabled) => {
      calls.push(`${id} ${enabled}`);
      stored = stored.map((s) => (s.id === id ? { ...s, enabled } : s));
      return stored.find((s) => s.id === id)!;
    },
  };
  const ui = (current = await screen(io));

  const before = ui.captureCharFrame();
  expect(before).toContain("Nightly scan");
  expect(before).toContain("every day · next 11-13 02:00");
  expect(before).toContain("Weekly review");

  ui.mockInput.pressEnter();
  await ui.settle();
  await ui.settle();

  expect(calls).toEqual(["one false"]);
  const after = ui.captureCharFrame();
  expect(after).toMatch(/Nightly scan\s+paused/);
  expect(after).toContain('"Nightly scan" is paused.');

  ui.mockInput.pressEnter();
  await ui.settle();
  await ui.settle();
  expect(calls).toEqual(["one false", "one true"]);
});

test("with nothing scheduled the dialog says how to add one", async () => {
  const ui = (current = await screen({ list: async () => [], setEnabled: async () => schedule("x") }));

  expect(ui.captureCharFrame()).toContain("Nothing is scheduled.");
  expect(ui.captureCharFrame()).toContain("nightcode schedule add scan . --every 1d");
});
