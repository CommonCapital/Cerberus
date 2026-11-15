import { SEVERITIES, type FailOn, type Severity } from "./args";

// Reads the findings out of the SARIF log the server builds, and decides
// whether they fail a build. The same file a code-scanning dashboard reads is
// what the gate counts, so the two can never disagree.

export type GateFinding = {
  severity: Severity;
  title: string;
  file: string;
  line: number;
  /** A model's candidate rather than a rule's match. */
  fromModel: boolean;
  /** In tests or documentation, not production code. */
  inTestsOrDocs: boolean;
  /** The target's own code marks this line to be ignored. */
  ignoredInCode: boolean;
  /** An operator recorded a decision on it (accepted risk, false positive). */
  decided: boolean;
};

const record = (value: unknown): Record<string, unknown> =>
  value && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : {};
const array = (value: unknown): unknown[] => (Array.isArray(value) ? value : []);
const text = (value: unknown) => (typeof value === "string" ? value : "");

/** For a log written before results carried their severity: the nearest reading of SARIF's three levels. */
const severityOfLevel = (level: string): Severity => (level === "error" ? "high" : level === "warning" ? "medium" : "low");

export function readSarif(log: unknown): GateFinding[] {
  const findings: GateFinding[] = [];
  for (const run of array(record(log).runs)) {
    for (const entry of array(record(run).results)) {
      const result = record(entry);
      const properties = record(result.properties);
      const location = record(record(array(result.locations)[0]).physicalLocation);
      const kinds = array(result.suppressions).map((s) => text(record(s).kind));
      const severity = text(properties.severity);
      let file = text(record(location.artifactLocation).uri);
      try {
        file = decodeURIComponent(file);
      } catch {
        // Not an escaped path: shown as it is.
      }
      findings.push({
        severity: SEVERITIES.includes(severity as Severity) ? (severity as Severity) : severityOfLevel(text(result.level)),
        title: text(record(result.message).text),
        file,
        line: Number(record(location.region).startLine) || 0,
        fromModel: properties.source === "model",
        inTestsOrDocs: typeof properties.context === "string" && properties.context !== "",
        ignoredInCode: kinds.includes("inSource"),
        decided: kinds.includes("external"),
      });
    }
  }
  return findings;
}

export type Summary = {
  /** Everything in the export. */
  total: number;
  /** What the gate looks at, most severe first. */
  counted: GateFinding[];
  bySeverity: Record<Severity, number>;
  ignoredInCode: number;
  decided: number;
  /** Left out because they are in tests or documentation (zero when those are included). */
  inTestsOrDocs: number;
  /** How many of the counted ones are a model's candidates. */
  fromModel: number;
};

/**
 * What counts: findings nobody has dealt with, in production code. A line the
 * target marks to be ignored and a finding an operator has decided on are left
 * out, as in the review report; so are tests and documentation unless asked for.
 */
export function summarize(findings: GateFinding[], options: { includeTests: boolean }): Summary {
  const open = findings.filter((f) => !f.ignoredInCode && !f.decided);
  const counted = open
    .filter((f) => options.includeTests || !f.inTestsOrDocs)
    .sort((a, b) => SEVERITIES.indexOf(a.severity) - SEVERITIES.indexOf(b.severity));
  const bySeverity = Object.fromEntries(SEVERITIES.map((s) => [s, counted.filter((f) => f.severity === s).length])) as Record<Severity, number>;
  return {
    total: findings.length,
    counted,
    bySeverity,
    ignoredInCode: findings.filter((f) => f.ignoredInCode).length,
    decided: findings.filter((f) => f.decided && !f.ignoredInCode).length,
    inTestsOrDocs: open.length - counted.length,
    fromModel: counted.filter((f) => f.fromModel).length,
  };
}

/** The counted findings that fail the build. */
export function failing(summary: Summary, failOn: FailOn): GateFinding[] {
  if (failOn === "none") return [];
  const threshold = SEVERITIES.indexOf(failOn);
  return summary.counted.filter((f) => SEVERITIES.indexOf(f.severity) <= threshold);
}
