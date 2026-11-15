import { TextAttributes } from "@opentui/core";
import { useTheme } from "../providers/theme";
import { usePromptConfig } from "../providers/prompt-config";
import { Mode } from "@cerberus/shared";
import { useCurrentEngagement } from "../lib/cerberus/engagement";
import { useWaitingCount } from "../lib/cerberus/waiting";
import { backendStatus } from "../lib/backend-fallback";

export function StatusBar() {
  const { mode, model } = usePromptConfig();
  const { colors } = useTheme();
  const engagement = useCurrentEngagement();
  const waiting = useWaitingCount();
  const backend = backendStatus();

  return (
    <box flexDirection="row" gap={1}>
      
      <text fg={mode === Mode.PLAN ? colors.planMode : colors.primary}>
        {mode === Mode.PLAN ? "Plan" : "Build"}
      </text>

      <text attributes={TextAttributes.DIM} fg={colors.dimSeparator}>
        ›
      </text>
      <text>{model}</text>
      {backend?.kind === "started" ? (
        <text attributes={TextAttributes.DIM}>· TypeScript server (chat only)</text>
      ) : backend?.kind === "not-started" ? (
        <text fg={colors.planMode}>· no server: {backend.reason}</text>
      ) : null}
      {waiting > 0 ? (
        <text fg={colors.planMode}>
          ! {waiting} run{waiting === 1 ? "" : "s"} waiting · /runs
        </text>
      ) : null}
      {engagement ? (
        <>
          <box flexGrow={1} />
          <text attributes={TextAttributes.DIM}>engagement</text>
          <text>{engagement.name}</text>
        </>
      ) : null}
    </box>
  );
};
