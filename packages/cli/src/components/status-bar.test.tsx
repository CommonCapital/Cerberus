import { afterAll, expect, test } from "bun:test";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { testRender } from "@opentui/react/test-utils";

// Never the operator's real settings directory.
const home = mkdtempSync(join(tmpdir(), "cerberus-test-"));
process.env.CERBERUS_HOME = home;
afterAll(() => rmSync(home, { recursive: true, force: true }));

const { StatusBar } = await import("./status-bar");
const { ThemeProvider } = await import("../providers/theme");
const { PromptConfigProvider } = await import("../providers/prompt-config");
const { setCurrentEngagement } = await import("../lib/cerberus/engagement");
const { setWaitingCount } = await import("../lib/cerberus/waiting");

async function frame() {
  const { renderOnce, captureCharFrame, renderer } = await testRender(
    <ThemeProvider>
      <PromptConfigProvider>
        <StatusBar />
      </PromptConfigProvider>
    </ThemeProvider>,
    { width: 90, height: 3 },
  );
  await renderOnce();
  const text = captureCharFrame();
  renderer.destroy();
  return text;
}

test("the status bar names the engagement runs go under, when there is one", async () => {
  process.env.CERBERUS_HOME = home;
  setCurrentEngagement(null);
  expect(await frame()).not.toContain("engagement");

  setCurrentEngagement({ id: "e-1", name: "Payments audit" });
  expect(await frame()).toContain("engagement Payments audit");
  setCurrentEngagement(null);
});

test("the status bar says how many runs are waiting for the operator", async () => {
  setWaitingCount(0);
  expect(await frame()).not.toContain("waiting");

  setWaitingCount(1);
  expect(await frame()).toContain("! 1 run waiting · /runs");
  setWaitingCount(3);
  expect(await frame()).toContain("! 3 runs waiting · /runs");
  setWaitingCount(0);
});
