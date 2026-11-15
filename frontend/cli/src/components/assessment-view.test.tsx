import { expect, test } from "bun:test";
import { testRender } from "@opentui/react/test-utils";
import { AssessmentView, canAccept, effectiveAnswer, needsActionPlan } from "./assessment-view";
import { ThemeProvider } from "../providers/theme";
import type { Assessment, AssessmentControl } from "../lib/cerberus/api";

const control = (controlId: string, title: string, extra: Partial<AssessmentControl> = {}): AssessmentControl => ({
  controlId, title, section: "S", verdict: "NotAssessed", rationale: "No automated signal; needs manual review.",
  response: null, suggestion: null, ...extra,
});

const suggestion = (controlId: string, answer: string, evidence: { file: string; line: number; text: string }[] = []) => ({
  controlId, suggestion: answer, reasoning: "Passwords are compared in plain text.", evidence, model: "llama3.1",
});

const assessment: Assessment = {
  id: "a-1", catalogId: "owasp-top10", targetPath: "services/api", createdAt: "2026-11-12T10:00:00Z",
  summary: { totalControls: 4, compliant: 1, nonCompliant: 1, notAssessed: 2, score: 50 },
  effective: { compliant: 1, partiallyCompliant: 0, nonCompliant: 1, notApplicable: 1, unanswered: 1, operatorAnswered: 1 },
  controls: [
    control("A01", "Broken Access Control", { response: { verdict: "Not applicable", justification: "No users.", actionPlan: null, timeLimitDays: null } }),
    control("A02", "Cryptographic Failures", { verdict: "NonCompliant", rationale: "1 finding in mapped categories." }),
    control("A03", "Injection", { verdict: "Compliant", rationale: "No findings in mapped categories." }),
    control("A07", "Authentication Failures", { suggestion: suggestion("A07", "Non-compliant", [{ file: "auth.py", line: 12, text: "if pw == stored:" }]) }),
  ],
};

async function frame(model: Assessment | null, selected = 0, error: string | null = null) {
  const { renderOnce, captureCharFrame, renderer } = await testRender(
    <ThemeProvider>
      <AssessmentView assessment={model} selected={selected} error={error} />
    </ThemeProvider>,
    { width: 110, height: 32 },
  );
  await renderOnce();
  const text = captureCharFrame();
  renderer.destroy();
  return text;
}

test("the answer that counts is the operator's, else the scanner's, else none", () => {
  expect(effectiveAnswer(assessment.controls[0]!)).toEqual({ text: "Not applicable", by: "you" });
  expect(effectiveAnswer(assessment.controls[1]!)).toEqual({ text: "Non-compliant", by: "scanner" });
  expect(effectiveAnswer(assessment.controls[3]!)).toEqual({ text: "Not answered", by: "nobody" });
});

test("a suggestion can be accepted unless the model could not tell; short of compliant it needs a plan", () => {
  expect(canAccept(assessment.controls[3])).toBe(true);
  expect(needsActionPlan(assessment.controls[3]!)).toBe(true);
  expect(canAccept(control("X", "x", { suggestion: suggestion("X", "Cannot tell") }))).toBe(false);
  expect(needsActionPlan(control("X", "x", { suggestion: suggestion("X", "Compliant") }))).toBe(false);
  expect(canAccept(assessment.controls[1])).toBe(false);
});

test("the assessment shows where it stands and every control with the answer that counts", async () => {
  const text = await frame(assessment);

  expect(text).toContain("Compliance assessment · owasp-top10 · services/api");
  expect(text).toContain("Now: 1 compliant · 0 partial · 1 non-compliant · 1 not applicable · 1 not answered");
  expect(text).toContain("Scanner alone judged 2 of 4 (score 50%); you answered 1.");
  expect(text).toContain("› A01");
  expect(text).toMatch(/A02 +Cryptographic Failures +Non-compliant/);
  expect(text).toMatch(/A07 +Authentication Failures +suggested: Non-compliant +Not answered/);
  // The highlighted control's detail: the operator's answer.
  expect(text).toContain("Your answer: Not applicable");
  expect(text).toContain("No users.");
});

test("the highlighted control shows what the model suggested, why, and where", async () => {
  const text = await frame(assessment, 3);

  expect(text).toContain("› A07");
  expect(text).toContain("Suggested by llama3.1: Non-compliant");
  expect(text).toContain("Passwords are compared in plain text.");
  expect(text).toContain("auth.py:12 if pw == stored:");
});

test("before it has loaded, and when loading failed, it says so", async () => {
  expect(await frame(null)).toContain("Loading the assessment…");
  expect(await frame(null, 0, "Assessment not found")).toContain("Assessment not found");
});
