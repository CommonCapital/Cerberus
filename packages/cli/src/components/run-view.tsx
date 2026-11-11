import { TextAttributes } from "@opentui/core";
import { useTheme } from "../providers/theme";
import {
  isFinished,
  severityCounts,
  type Finding,
  type RunView as RunViewModel,
  type Step,
  type ToolActivity,
} from "../lib/cerberus/run-state";
import type { JobState } from "../lib/cerberus/api";

// What the operator sees of one run: its state, task list, progress, findings,
// model output, and — when the run is waiting on them — the approval or
// question to act on. Purely presentational; the screen owns data and keys.

const MAX_FINDINGS_SHOWN = 10;
const MAX_TEXT_LINES = 6;
const MAX_TOOLS_SHOWN = 8;

/** A tool call on one line: its name and the argument that says most about it. */
export function describeTool(tool: ToolActivity): string {
  const input = (tool.input ?? {}) as Record<string, unknown>;
  const main =
    tool.name === "reportFinding"
      ? `${String(input.file ?? "?")}:${String(input.line ?? "?")} ${String(input.title ?? "")}`
      : (["pattern", "query", "name", "path"] as const)
          .filter((key) => typeof input[key] === "string")
          .map((key) => String(input[key]))
          .join(" in ");
  return `${tool.name}${main ? ` ${main}` : ""}`;
}

type Props = {
  view: RunViewModel;
  /** Highlighted option of the pending question. */
  selectedOption: number;
  error?: string | null;
  /** The finished review's report (Markdown), once loaded. */
  report?: string | null;
};

const STATE_LABEL: Record<JobState, string> = {
  AwaitingApproval: "Waiting for your approval",
  Queued: "Queued",
  Running: "Running",
  AwaitingInput: "Waiting for your answer",
  Succeeded: "Succeeded",
  Failed: "Failed",
  Cancelled: "Cancelled",
  TimedOut: "Timed out",
};

const STEP_MARK: Record<Step["status"], string> = { pending: "○", running: "▶", completed: "✔", skipped: "–" };

const TITLE: Record<string, string> = { scan: "Scan", assess: "Compliance assessment", triage: "Finding triage",
  review: "Security review",
  sast: "Code review by a model",
  evidence: "Compliance evidence by a model",
};

const RESULT: Record<string, string> = { scan: "scan", assess: "assessment", triage: "scan", review: "review", sast: "agent run", evidence: "assessment" };

export function RunView({ view, selectedOption, error, report }: Props) {
  const { colors } = useTheme();
  const { job } = view;

  const stateColor = (state: JobState) =>
    state === "Succeeded"
      ? colors.success
      : state === "Failed" || state === "TimedOut" || state === "Cancelled"
        ? colors.error
        : state === "AwaitingApproval" || state === "AwaitingInput"
          ? colors.planMode
          : colors.info;

  const severityColor = (severity: Finding["severity"]) =>
    severity === "Critical" || severity === "High" ? colors.error : severity === "Medium" ? colors.planMode : undefined;

  if (!job) {
    return (
      <box flexDirection="column" gap={1}>
        <text attributes={TextAttributes.DIM}>{error ?? "Loading the run…"}</text>
      </box>
    );
  }

  const shown = view.findings.slice(-MAX_FINDINGS_SHOWN);
  const hidden = view.findings.length - shown.length;
  const lastText = view.text[view.text.length - 1];
  const textLines = lastText ? lastText.text.split("\n").slice(-MAX_TEXT_LINES) : [];

  return (
    <box flexDirection="column" gap={1} width="100%">
      {/* Header: what is running, on what, and its state */}
      <box flexDirection="row" gap={1}>
        <text attributes={TextAttributes.BOLD}>{TITLE[job.type] ?? job.type}</text>
        <text attributes={TextAttributes.DIM}>·</text>
        <text>{job.targetPath}</text>
        <box flexGrow={1} />
        <text fg={stateColor(job.state)}>● {STATE_LABEL[job.state]}</text>
      </box>

      {/* Task list */}
      {view.steps.length > 0 ? (
        <box flexDirection="column">
          {view.steps.map((step) => (
            <box key={step.id} flexDirection="row" gap={1}>
              <text
                fg={
                  step.status === "completed" ? colors.success : step.status === "running" ? colors.info : colors.dimSeparator
                }
              >
                {STEP_MARK[step.status]}
              </text>
              <text attributes={step.status === "running" ? TextAttributes.BOLD : TextAttributes.DIM}>
                {step.title}
                {step.status === "skipped" ? " (skipped)" : ""}
              </text>
            </box>
          ))}
          {view.progress?.message ? (
            <text attributes={TextAttributes.DIM}>  {view.progress.message}</text>
          ) : null}
        </box>
      ) : null}

      {/* What the agent is doing: its tool calls, latest last */}
      {view.tools.length > 0 ? (
        <box flexDirection="column">
          <text attributes={TextAttributes.BOLD}>Tool calls ({view.tools.length})</text>
          {view.tools.length > MAX_TOOLS_SHOWN ? (
            <text attributes={TextAttributes.DIM}>  … {view.tools.length - MAX_TOOLS_SHOWN} earlier</text>
          ) : null}
          {view.tools.slice(-MAX_TOOLS_SHOWN).map((tool) => (
            <box key={tool.id} flexDirection="row" gap={1} height={1} overflow="hidden">
              <text fg={tool.output === undefined ? colors.info : tool.isError ? colors.error : colors.success}>
                {tool.output === undefined ? "▶" : tool.isError ? "✘" : "✔"}
              </text>
              <text>{describeTool(tool)}</text>
              {tool.isError && tool.output ? <text attributes={TextAttributes.DIM}>— {tool.output}</text> : null}
            </box>
          ))}
        </box>
      ) : null}

      {/* Findings, as they arrive */}
      {view.findings.length > 0 ? (
        <box flexDirection="column">
          <box flexDirection="row" gap={1}>
            <text attributes={TextAttributes.BOLD}>Findings ({view.findings.length})</text>
            <text attributes={TextAttributes.DIM}>
              {severityCounts(view.findings)
                .map((entry) => `${entry.severity} ${entry.count}`)
                .join(" · ")}
            </text>
          </box>
          {hidden > 0 ? <text attributes={TextAttributes.DIM}>  … {hidden} earlier</text> : null}
          {shown.map((finding) => (
            <box key={finding.id} flexDirection="row" gap={1} height={1} overflow="hidden">
              <text fg={severityColor(finding.severity)}>{finding.severity.toUpperCase().padEnd(8)}</text>
              <text>{finding.title}</text>
              <text attributes={TextAttributes.DIM}>
                {finding.file}:{finding.line}
                {finding.context ? ` [${finding.context}]` : ""}
              </text>
            </box>
          ))}
        </box>
      ) : null}

      {/* Model output while it is being written */}
      {textLines.length > 0 && (!isFinished(view) || job.type === "sast" || job.type === "evidence") ? (
        <box flexDirection="column" border={["left"]} borderColor={colors.thinkingBorder} paddingLeft={1}>
          {textLines.map((line, index) => (
            <text key={index} fg={colors.thinking} attributes={TextAttributes.DIM}>
              {line}
            </text>
          ))}
        </box>
      ) : null}

      {/* Model verdicts */}
      {view.triage.length > 0 ? (
        <box flexDirection="column">
          <text attributes={TextAttributes.BOLD}>Model review ({view.triage.length})</text>
          {view.triage.map((opinion) => (
            <box key={opinion.findingId} flexDirection="column">
              <box flexDirection="row" gap={1}>
                <text
                  fg={
                    opinion.verdict === "TruePositive"
                      ? colors.error
                      : opinion.verdict === "FalsePositive"
                        ? colors.success
                        : colors.planMode
                  }
                >
                  {opinion.verdict === "TruePositive"
                    ? "REAL"
                    : opinion.verdict === "FalsePositive"
                      ? "HARMLESS"
                      : "UNSURE"}
                </text>
                <text attributes={TextAttributes.DIM}>{opinion.findingId.split(":").slice(1).join(":")}</text>
              </box>
              <text attributes={TextAttributes.DIM}>  {opinion.reasoning}</text>
            </box>
          ))}
        </box>
      ) : null}

      {/* Suggested answers for compliance controls */}
      {view.suggestions.length > 0 ? (
        <box flexDirection="column">
          <text attributes={TextAttributes.BOLD}>Suggested answers ({view.suggestions.length})</text>
          {view.suggestions.map((suggestion) => {
            const answer = suggestion.suggestion.toLowerCase();
            return (
              <box key={suggestion.controlId} flexDirection="column">
                <box flexDirection="row" gap={1}>
                  <text>{suggestion.controlId}</text>
                  <text
                    fg={
                      answer === "compliant"
                        ? colors.success
                        : answer.startsWith("non") || answer.startsWith("partial")
                          ? colors.error
                          : colors.planMode
                    }
                  >
                    {suggestion.suggestion}
                  </text>
                  {suggestion.evidence.length > 0 ? (
                    <text attributes={TextAttributes.DIM}>
                      {suggestion.evidence.map((e) => `${e.file}:${e.line}`).join(", ")}
                    </text>
                  ) : null}
                </box>
                <text attributes={TextAttributes.DIM}>  {suggestion.reasoning}</text>
              </box>
            );
          })}
          <text attributes={TextAttributes.DIM}>  A model's suggestions change nothing until you accept them in the assessment.</text>
        </box>
      ) : null}

      {view.notes.map((note, index) => (
        <text key={`note-${index}`} attributes={TextAttributes.DIM}>
          ℹ {note}
        </text>
      ))}

      {view.resolved.map((line, index) => (
        <text key={`resolved-${index}`} attributes={TextAttributes.DIM}>
          ↳ {line}
        </text>
      ))}

      {/* Human in the loop: the run is waiting for a decision */}
      {view.approval ? (
        <box flexDirection="column" border borderColor={colors.planMode} paddingX={1}>
          <text fg={colors.planMode} attributes={TextAttributes.BOLD}>
            Approval needed
          </text>
          <text>{view.approval.title}</text>
          {view.approval.detail.split("\n").map((line, index) => (
            <text key={index} attributes={TextAttributes.DIM}>
              {line}
            </text>
          ))}
          <box flexDirection="row" gap={1} marginTop={1}>
            <text fg={colors.success}>a</text>
            <text attributes={TextAttributes.DIM}>approve</text>
            <text fg={colors.success}>r</text>
            <text attributes={TextAttributes.DIM}>approve and always allow</text>
            <text fg={colors.error}>d</text>
            <text attributes={TextAttributes.DIM}>deny</text>
          </box>
        </box>
      ) : null}

      {view.question ? (
        <box flexDirection="column" border borderColor={colors.planMode} paddingX={1}>
          <text fg={colors.planMode} attributes={TextAttributes.BOLD}>
            Question
          </text>
          <text>{view.question.text}</text>
          <box flexDirection="column" marginTop={1}>
            {view.question.options.map((option, index) => {
              const selected = index === selectedOption;
              return (
                <box key={option.label} flexDirection="column">
                  <text fg={selected ? colors.primary : undefined} attributes={selected ? TextAttributes.BOLD : 0}>
                    {selected ? "›" : " "} {option.label}
                  </text>
                  {option.description ? (
                    <text attributes={TextAttributes.DIM}>    {option.description}</text>
                  ) : null}
                </box>
              );
            })}
          </box>
          <box flexDirection="row" gap={1} marginTop={1}>
            <text>↑↓</text>
            <text attributes={TextAttributes.DIM}>select</text>
            <text>enter</text>
            <text attributes={TextAttributes.DIM}>answer</text>
          </box>
        </box>
      ) : null}

      {job.error ? <text fg={colors.error}>{job.error}</text> : null}
      {error ? <text fg={colors.error}>{error}</text> : null}

      {isFinished(view) && job.state === "Succeeded" && job.resultId ? (
        <text attributes={TextAttributes.DIM}>
          Result: {RESULT[job.type] ?? job.type} {job.resultId}
        </text>
      ) : null}

      {/* A review ends in a report: show it as written */}
      {report ? (
        <box flexDirection="column" border={["top"]} borderColor={colors.dimSeparator}>
          {report.split("\n").map((line, index) => (
            <text key={index} attributes={line.startsWith("#") ? TextAttributes.BOLD : 0}>
              {line.length > 0 ? line : " "}
            </text>
          ))}
        </box>
      ) : null}
    </box>
  );
}
