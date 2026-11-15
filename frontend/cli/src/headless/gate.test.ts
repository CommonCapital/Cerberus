import { expect, test } from "bun:test";
import { failing, readSarif, summarize } from "./gate";

type Extra = { level?: string; context?: string; source?: string; suppressions?: { kind: string }[]; severity?: string | null };

const result = (severity: string, file: string, line: number, title: string, extra: Extra = {}) => ({
  ruleId: "r",
  level: extra.level ?? "warning",
  message: { text: title },
  locations: [{ physicalLocation: { artifactLocation: { uri: file }, region: { startLine: line } } }],
  ...(extra.suppressions ? { suppressions: extra.suppressions } : {}),
  properties: {
    ...(extra.severity === null ? {} : { severity }),
    ...(extra.context ? { context: extra.context } : {}),
    ...(extra.source ? { source: extra.source } : {}),
  },
});
const log = (...runs: unknown[][]) => ({ version: "2.1.0", runs: runs.map((results) => ({ results })) });

test("findings are read from every run of the log, with where they are and who reported them", () => {
  const findings = readSarif(
    log(
      [result("critical", "app/a%20b.py", 3, "Command injection")],
      [result("medium", "app/c.py", 9, "Weak hash: md5 for passwords", { source: "model" })],
    ),
  );

  expect(findings).toEqual([
    { severity: "critical", title: "Command injection", file: "app/a b.py", line: 3, fromModel: false, inTestsOrDocs: false, ignoredInCode: false, decided: false },
    { severity: "medium", title: "Weak hash: md5 for passwords", file: "app/c.py", line: 9, fromModel: true, inTestsOrDocs: false, ignoredInCode: false, decided: false },
  ]);
});

test("a log without exact severities is read by its level", () => {
  const findings = readSarif(
    log([
      result("", "a", 1, "e", { level: "error", severity: null }),
      result("", "a", 2, "w", { level: "warning", severity: null }),
      result("", "a", 3, "n", { level: "note", severity: null }),
    ]),
  );
  expect(findings.map((f) => f.severity)).toEqual(["high", "medium", "low"]);
});

test("something that is not a SARIF log has no findings, and does not throw", () => {
  expect(readSarif(null)).toEqual([]);
  expect(readSarif({ runs: "no" })).toEqual([]);
  expect(readSarif({ runs: [{ results: [{}] }] })).toHaveLength(1);
});

const mixed = readSarif(
  log([
    result("low", "src/a.py", 1, "Low"),
    result("critical", "src/b.py", 2, "Critical"),
    result("high", "src/c.py", 3, "Ignored in code", { suppressions: [{ kind: "inSource" }] }),
    result("high", "src/d.py", 4, "Accepted risk", { suppressions: [{ kind: "external" }] }),
    result("high", "tests/e.py", 5, "In a test", { context: "test" }),
    result("medium", "src/f.py", 6, "From a model", { source: "model" }),
  ]),
);

test("what counts is open findings in production code, most severe first", () => {
  const summary = summarize(mixed, { includeTests: false });

  expect(summary.total).toBe(6);
  expect(summary.counted.map((f) => f.title)).toEqual(["Critical", "From a model", "Low"]);
  expect(summary.bySeverity).toEqual({ critical: 1, high: 0, medium: 1, low: 1, info: 0 });
  expect(summary).toMatchObject({ ignoredInCode: 1, decided: 1, inTestsOrDocs: 1, fromModel: 1 });
});

test("tests and documentation count when asked for; ignored and decided findings never do", () => {
  const summary = summarize(mixed, { includeTests: true });

  expect(summary.counted.map((f) => f.title)).toEqual(["Critical", "In a test", "From a model", "Low"]);
  expect(summary.inTestsOrDocs).toBe(0);
});

test("the gate fails on findings at or above the threshold", () => {
  const summary = summarize(mixed, { includeTests: false });

  expect(failing(summary, "critical").map((f) => f.title)).toEqual(["Critical"]);
  expect(failing(summary, "high").map((f) => f.title)).toEqual(["Critical"]);
  expect(failing(summary, "medium").map((f) => f.title)).toEqual(["Critical", "From a model"]);
  expect(failing(summary, "info")).toHaveLength(3);
  expect(failing(summary, "none")).toEqual([]);
});
