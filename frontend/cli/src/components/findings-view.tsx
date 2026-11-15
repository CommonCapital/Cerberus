import { TextAttributes } from "@opentui/core";
import { useTheme } from "../providers/theme";
import type { ScanDetail, ScanFinding } from "../lib/cerberus/api";

// The findings of one scan, for the operator to go through and decide on.
// Purely presentational; the screen owns data and keys.

const VISIBLE = 10;
const SEVERITY = ["Critical", "High", "Medium", "Low", "Info"];

/** Open findings first, most severe first; then what the operator has decided; then what the target suppressed. */
export function orderFindings(findings: ScanFinding[]): ScanFinding[] {
  const group = (f: ScanFinding) => (f.suppressed ? 2 : f.decision ? 1 : 0);
  return [...findings].sort(
    (a, b) => group(a) - group(b) || SEVERITY.indexOf(a.severity) - SEVERITY.indexOf(b.severity) || a.file.localeCompare(b.file) || a.line - b.line,
  );
}

export const openCount = (findings: ScanFinding[]) => findings.filter((f) => !f.decision && !f.suppressed).length;

export const decisionLabel = (status: "accepted-risk" | "false-positive") =>
  status === "accepted-risk" ? "accepted risk" : "false positive";

type Props = {
  scan: ScanDetail | null;
  /** In the order of `orderFindings`. */
  findings: ScanFinding[];
  selected: number;
  error?: string | null;
};

export function FindingsView({ scan, findings, selected, error }: Props) {
  const { colors } = useTheme();

  if (!scan) return <text attributes={TextAttributes.DIM}>{error ?? "Loading the findings…"}</text>;

  const severityColor = (severity: ScanFinding["severity"]) =>
    severity === "Critical" || severity === "High" ? colors.error : severity === "Medium" ? colors.planMode : undefined;

  const first = Math.max(0, Math.min(selected - Math.floor(VISIBLE / 2), findings.length - VISIBLE));
  const current = findings[selected];
  const decided = findings.filter((f) => f.decision).length;

  return (
    <box flexDirection="column" gap={1} width="100%">
      <box flexDirection="row" gap={1}>
        <text attributes={TextAttributes.BOLD}>Findings</text>
        <text attributes={TextAttributes.DIM}>·</text>
        <text>{scan.targetPath}</text>
        <box flexGrow={1} />
        <text>
          {openCount(findings)} open · {decided} decided
        </text>
      </box>

      {findings.length === 0 ? (
        <text attributes={TextAttributes.DIM}>The scan found nothing.</text>
      ) : (
        <box flexDirection="column">
          {first > 0 ? <text attributes={TextAttributes.DIM}>  … {first} above</text> : null}
          {findings.slice(first, first + VISIBLE).map((finding, offset) => {
            const isSelected = first + offset === selected;
            const settled = finding.decision !== null || finding.suppressed;
            return (
              <box key={`${finding.id}-${first + offset}`} flexDirection="row" gap={1} height={1} overflow="hidden">
                <text fg={isSelected ? colors.primary : undefined}>{isSelected ? "›" : " "}</text>
                <text fg={settled ? undefined : severityColor(finding.severity)} attributes={settled ? TextAttributes.DIM : 0}>
                  {finding.severity.toUpperCase().padEnd(8)}
                </text>
                <text attributes={isSelected ? TextAttributes.BOLD : settled ? TextAttributes.DIM : 0}>{finding.title}</text>
                <text attributes={TextAttributes.DIM}>
                  {finding.file}:{finding.line}
                </text>
                <box flexGrow={1} />
                <text attributes={TextAttributes.DIM}>
                  {finding.decision
                    ? decisionLabel(finding.decision.status)
                    : finding.suppressed
                      ? "suppressed in the code"
                      : finding.context
                        ? finding.context
                        : ""}
                </text>
              </box>
            );
          })}
          {first + VISIBLE < findings.length ? (
            <text attributes={TextAttributes.DIM}>  … {findings.length - first - VISIBLE} below</text>
          ) : null}
        </box>
      )}

      {current ? (
        <box flexDirection="column" border={["top"]} borderColor={colors.dimSeparator}>
          <text attributes={TextAttributes.BOLD}>{current.title}</text>
          <text attributes={TextAttributes.DIM}>
            {current.file}:{current.line} · {current.category}
            {current.context ? ` · ${current.context} code` : ""}
          </text>
          <text>  {current.evidence}</text>
          {current.remediation ? <text attributes={TextAttributes.DIM}>Fix: {current.remediation}</text> : null}
          {current.suppressed ? (
            <text attributes={TextAttributes.DIM}>The code itself marks this line to be ignored (cerberus:ignore).</text>
          ) : null}
          {current.decision ? (
            <text fg={colors.planMode}>
              You decided: {decisionLabel(current.decision.status)} — {current.decision.note}
            </text>
          ) : null}
        </box>
      ) : null}

      {error ? <text fg={colors.error}>{error}</text> : null}
    </box>
  );
}
