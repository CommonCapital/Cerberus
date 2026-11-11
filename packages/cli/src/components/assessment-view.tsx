import { TextAttributes } from "@opentui/core";
import { useTheme } from "../providers/theme";
import type { Assessment, AssessmentControl } from "../lib/cerberus/api";

// One compliance assessment: where it stands, its controls, and for the
// highlighted control what the scanner said, what a model suggested and what
// the operator answered. Purely presentational; the screen owns data and keys.

const VISIBLE_CONTROLS = 10;

/** The answer that counts for a control: the operator's if there is one, else the scanner's. */
export function effectiveAnswer(control: AssessmentControl): { text: string; by: "you" | "scanner" | "nobody" } {
  if (control.response) return { text: control.response.verdict, by: "you" };
  if (control.verdict === "Compliant") return { text: "Compliant", by: "scanner" };
  if (control.verdict === "NonCompliant") return { text: "Non-compliant", by: "scanner" };
  return { text: "Not answered", by: "nobody" };
}

/** Whether accepting this control's suggestion needs an action plan first (anything short of compliant does). */
export function needsActionPlan(control: AssessmentControl): boolean {
  const answer = (control.suggestion?.suggestion ?? "").toLowerCase();
  return answer.startsWith("non") || answer.startsWith("partial");
}

export const canAccept = (control: AssessmentControl | undefined) =>
  !!control?.suggestion && control.suggestion.suggestion !== "Cannot tell";

type Props = {
  assessment: Assessment | null;
  selected: number;
  error?: string | null;
};

export function AssessmentView({ assessment, selected, error }: Props) {
  const { colors } = useTheme();

  if (!assessment) {
    return <text attributes={TextAttributes.DIM}>{error ?? "Loading the assessment…"}</text>;
  }

  const answerColor = (text: string) => {
    const answer = text.toLowerCase();
    return answer === "compliant"
      ? colors.success
      : answer.startsWith("non") || answer.startsWith("partial")
        ? colors.error
        : answer === "not answered" || answer === "cannot tell"
          ? colors.planMode
          : undefined;
  };

  const { controls, summary, effective } = assessment;
  const first = Math.max(0, Math.min(selected - Math.floor(VISIBLE_CONTROLS / 2), controls.length - VISIBLE_CONTROLS));
  const current = controls[selected];

  return (
    <box flexDirection="column" gap={1} width="100%">
      <box flexDirection="row" gap={1}>
        <text attributes={TextAttributes.BOLD}>Compliance assessment</text>
        <text attributes={TextAttributes.DIM}>·</text>
        <text>{assessment.catalogId}</text>
        <text attributes={TextAttributes.DIM}>·</text>
        <text>{assessment.targetPath}</text>
      </box>

      <box flexDirection="column">
        <text>
          Now: {effective.compliant} compliant · {effective.partiallyCompliant} partial · {effective.nonCompliant} non-compliant ·{" "}
          {effective.notApplicable} not applicable · {effective.unanswered} not answered
        </text>
        <text attributes={TextAttributes.DIM}>
          Scanner alone judged {summary.totalControls - summary.notAssessed} of {summary.totalControls}
          {summary.score === null ? "" : ` (score ${Math.round(summary.score)}%)`}; you answered {effective.operatorAnswered}.
        </text>
      </box>

      <box flexDirection="column">
        {first > 0 ? <text attributes={TextAttributes.DIM}>  … {first} above</text> : null}
        {controls.slice(first, first + VISIBLE_CONTROLS).map((control, offset) => {
          const isSelected = first + offset === selected;
          const answer = effectiveAnswer(control);
          return (
            <box key={control.controlId} flexDirection="row" gap={1} height={1} overflow="hidden">
              <text fg={isSelected ? colors.primary : undefined} attributes={isSelected ? TextAttributes.BOLD : 0}>
                {isSelected ? "›" : " "} {control.controlId.padEnd(8)}
              </text>
              <text attributes={isSelected ? TextAttributes.BOLD : 0}>{control.title}</text>
              <box flexGrow={1} />
              {control.suggestion && !control.response ? (
                <text attributes={TextAttributes.DIM}>suggested: {control.suggestion.suggestion}</text>
              ) : null}
              <text fg={answerColor(answer.text)}>{answer.text}</text>
            </box>
          );
        })}
        {first + VISIBLE_CONTROLS < controls.length ? (
          <text attributes={TextAttributes.DIM}>  … {controls.length - first - VISIBLE_CONTROLS} below</text>
        ) : null}
      </box>

      {current ? (
        <box flexDirection="column" border={["top"]} borderColor={colors.dimSeparator}>
          <text attributes={TextAttributes.BOLD}>
            {current.controlId} {current.title}
          </text>
          <text attributes={TextAttributes.DIM}>Scanner: {current.rationale}</text>
          {current.response ? (
            <box flexDirection="column">
              <box flexDirection="row" gap={1}>
                <text>Your answer:</text>
                <text fg={answerColor(current.response.verdict)}>{current.response.verdict}</text>
              </box>
              <text attributes={TextAttributes.DIM}>  {current.response.justification}</text>
              {current.response.actionPlan ? (
                <text attributes={TextAttributes.DIM}>  Plan: {current.response.actionPlan}</text>
              ) : null}
            </box>
          ) : null}
          {current.suggestion ? (
            <box flexDirection="column">
              <box flexDirection="row" gap={1}>
                <text>Suggested by {current.suggestion.model}:</text>
                <text fg={answerColor(current.suggestion.suggestion)}>{current.suggestion.suggestion}</text>
              </box>
              <text attributes={TextAttributes.DIM}>  {current.suggestion.reasoning}</text>
              {current.suggestion.evidence.map((e, index) => (
                <text key={index} attributes={TextAttributes.DIM}>
                  {"  "}
                  {e.file}:{e.line} {e.text}
                </text>
              ))}
            </box>
          ) : null}
        </box>
      ) : null}

      {error ? <text fg={colors.error}>{error}</text> : null}
    </box>
  );
}
