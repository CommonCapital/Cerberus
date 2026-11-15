import { expect, test } from "bun:test";
import { testRender } from "@opentui/react/test-utils";
import { ShellMessage } from "./shell-message";
import { ThemeProvider } from "../../providers/theme";
import type { ShellRecord } from "../../lib/chat-flow";

async function frame(record: ShellRecord) {
  const { renderOnce, captureCharFrame, renderer } = await testRender(
    <ThemeProvider>
      <ShellMessage record={record} />
    </ThemeProvider>,
    { width: 80, height: 24 },
  );
  await renderOnce();
  const text = captureCharFrame();
  renderer.destroy();
  return text;
}

test("a command the operator ran shows the command and what it printed", async () => {
  const text = await frame({ command: "git status -s", exitCode: 0, output: " M src/app.ts\n?? notes.md" });

  expect(text).toContain("$ git status -s");
  expect(text).toContain("M src/app.ts");
  expect(text).toContain("?? notes.md");
  expect(text).not.toContain("exit");
});

test("a failure and a timeout are pointed out; long output shows its end", async () => {
  expect(await frame({ command: "npm test", exitCode: 2, output: "1 failed" })).toContain("exit 2");
  expect(await frame({ command: "sleep 999", exitCode: 143, output: "", timedOut: true })).toContain("stopped: it ran too long");

  const long = await frame({ command: "seq 1 40", exitCode: 0, output: Array.from({ length: 40 }, (_, i) => `line ${i + 1}`).join("\n") });
  expect(long).toContain("… 28 earlier line(s)");
  expect(long).toContain("line 40");
  expect(long).not.toContain("line 28\n");
});
