import { expect, test } from "bun:test";
import { parseArgs, type RunOptions } from "./args";

const run = (line: string): RunOptions => {
  const parsed = parseArgs(line.split(" ").filter(Boolean));
  if (parsed.kind !== "run") throw new Error(`not a run: ${JSON.stringify(parsed)}`);
  return parsed.options;
};
const refusal = (line: string): string => {
  const parsed = parseArgs(line.split(" ").filter(Boolean));
  if (parsed.kind !== "usage") throw new Error(`not refused: ${JSON.stringify(parsed)}`);
  return parsed.message;
};

test("a bare scan: the current directory, fails on high, honours the target's ignore marks, approves nothing", () => {
  const options = run("run scan");

  expect(options.job).toEqual({ type: "scan", path: ".", sca: undefined, exclusions: "honour", engagementId: undefined });
  expect(options.failOn).toBe("high");
  expect(options.approve).toEqual([]);
  expect(options.timeoutMinutes).toBe(60);
  expect(options).toMatchObject({ includeTests: false, sarif: null, report: null, json: false, quiet: false, server: null });
});

test("options are read in either form, in any position", () => {
  const options = run("run --fail-on=medium scan services/api --sarif out/a.sarif --no-sca --exclusions ignore --json --engagement e1 --timeout 5");

  expect(options.job).toEqual({ type: "scan", path: "services/api", sca: false, exclusions: "ignore", engagementId: "e1" });
  expect(options).toMatchObject({ failOn: "medium", sarif: "out/a.sarif", json: true, timeoutMinutes: 5 });
});

test("a review carries its catalogs, models and agents", () => {
  const options = run("run review . --catalogs owasp-asvs,nist --model m1 --agent-model m2 --agents sast,evidence --report r.md");

  expect(options.job).toMatchObject({
    type: "review", path: ".", catalogs: ["owasp-asvs", "nist"], model: "m1", agentModel: "m2", agents: ["sast", "evidence"], exclusions: "honour",
  });
  expect(options.report).toBe("r.md");
});

test("an agent job needs its model named", () => {
  expect(refusal("run sast src")).toContain("--model");
  expect(run("run threat-model src --model m --max-steps 12").job).toMatchObject({ type: "threat-model", path: "src", model: "m", maxSteps: 12 });
});

test("approvals are given by name", () => {
  expect(run("run sast . --model m --approve send-code-to-cloud-model,run-job").approve).toEqual(["send-code-to-cloud-model", "run-job"]);
});

test("help is help wherever it stands", () => {
  expect(parseArgs(["--help"]).kind).toBe("help");
  expect(parseArgs(["help"]).kind).toBe("help");
  expect(parseArgs(["run", "scan", "-h"]).kind).toBe("help");
});

test("what cannot be meant is refused, not guessed", () => {
  expect(refusal("scan .")).toContain('Unknown command "scan"');
  expect(refusal("run")).toContain("Say what to run");
  expect(refusal("run pentest .")).toContain('"pentest" is not something run can start');
  expect(refusal("run scan a b")).toContain("One path at a time");
  expect(refusal("run scan --frobnicate")).toContain("Unknown option --frobnicate");
  expect(refusal("run scan --sarif")).toContain("--sarif needs a value");
  expect(refusal("run scan --sarif --json")).toContain("--sarif needs a value");
  expect(refusal("run scan --json=yes")).toContain("takes no value");
  expect(refusal("run scan --fail-on a --fail-on b")).toContain("given twice");
  expect(refusal("run scan --fail-on severe")).toContain("--fail-on must be one of");
  expect(refusal("run scan --timeout 0")).toContain("--timeout");
  expect(refusal("run sast . --model m --max-steps many")).toContain("--max-steps");
  expect(refusal("run review . --agents sast")).toContain("--agents needs --agent-model");
  expect(refusal("run review . --agent-model m --agents pentest")).toContain('"pentest" is not a review agent');
});

test("an option that would do nothing for this job is refused", () => {
  expect(refusal("run scan --model m")).toContain("--model does not apply to scan");
  expect(refusal("run index . --sarif a.sarif")).toContain("--sarif does not apply to index");
  expect(refusal("run threat-model . --model m --fail-on low")).toContain("--fail-on does not apply to threat-model");
  expect(refusal("run sast . --model m --no-sca")).toContain("--no-sca does not apply to sast");
});

test("nobody is here to ask, so exclusions cannot be 'ask'", () => {
  expect(refusal("run scan --exclusions ask")).toContain("honour or ignore");
});
