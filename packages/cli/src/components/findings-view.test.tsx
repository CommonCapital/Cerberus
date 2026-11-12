import { expect, test } from "bun:test";
import { testRender } from "@opentui/react/test-utils";
import { FindingsView, openCount, orderFindings } from "./findings-view";
import { ThemeProvider } from "../providers/theme";
import type { ScanDetail, ScanFinding } from "../lib/cerberus/api";

const finding = (id: string, severity: ScanFinding["severity"], extra: Partial<ScanFinding> = {}): ScanFinding => ({
  id, title: `Problem ${id}`, severity, category: "injection", file: "app.py", line: 1, evidence: "os.system(cmd)",
  remediation: "Use an argument list.", decision: null, ...extra,
});

const decision = (status: "accepted-risk" | "false-positive", note: string) => ({ id: "d-1", status, note, decidedAt: "2026-11-12T10:00:00Z" });

const findings: ScanFinding[] = [
  finding("low", "Low"),
  finding("decided", "Critical", { decision: decision("accepted-risk", "Internal tool only.") }),
  finding("hidden", "High", { suppressed: true }),
  finding("high", "High", { file: "b.py", line: 7 }),
  finding("test", "Medium", { context: "test" }),
];

const scan: ScanDetail = { id: "s-1", targetPath: "services/api", findingCount: 4, createdAt: "2026-11-12T10:00:00Z", findings };

async function frame(model: ScanDetail | null, list: ScanFinding[], selected = 0, error: string | null = null) {
  const { renderOnce, captureCharFrame, renderer } = await testRender(
    <ThemeProvider>
      <FindingsView scan={model} findings={list} selected={selected} error={error} />
    </ThemeProvider>,
    { width: 110, height: 30 },
  );
  await renderOnce();
  const text = captureCharFrame();
  renderer.destroy();
  return text;
}

test("open findings come first, most severe first; then decided ones; then what the code suppressed", () => {
  expect(orderFindings(findings).map((f) => f.id)).toEqual(["high", "test", "low", "decided", "hidden"]);
  expect(openCount(findings)).toBe(3);
});

test("the list says what is open and what is settled, and the detail shows the code and the fix", async () => {
  const text = await frame(scan, orderFindings(findings));

  expect(text).toContain("Findings · services/api");
  expect(text).toContain("3 open · 1 decided");
  expect(text).toMatch(/› HIGH +Problem high b\.py:7/);
  expect(text).toMatch(/CRITICAL Problem decided app\.py:1 +accepted risk/);
  expect(text).toMatch(/HIGH +Problem hidden app\.py:1 +suppressed in the code/);
  expect(text).toMatch(/MEDIUM +Problem test app\.py:1 +test/);
  expect(text).toContain("os.system(cmd)");
  expect(text).toContain("Fix: Use an argument list.");
});

test("a decided finding shows the decision and its reason", async () => {
  const text = await frame(scan, orderFindings(findings), 3);
  expect(text).toContain("You decided: accepted risk — Internal tool only.");
});

test("an empty scan, a loading one and a failed load each say so", async () => {
  expect(await frame({ ...scan, findings: [] }, [])).toContain("The scan found nothing.");
  expect(await frame(null, [])).toContain("Loading the findings…");
  expect(await frame(null, [], 0, "Scan not found")).toContain("Scan not found");
});
