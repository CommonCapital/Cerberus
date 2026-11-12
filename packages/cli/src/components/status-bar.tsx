import { TextAttributes } from "@opentui/core";
import { useTheme } from "../providers/theme";
import { usePromptConfig } from "../providers/prompt-config";
import { Mode } from "@cerberus/shared";
import { useCurrentEngagement } from "../lib/cerberus/engagement";
import { useWaitingCount } from "../lib/cerberus/waiting";

export function StatusBar() {
  const { mode, model } = usePromptConfig();
  const { colors } = useTheme();
  const engagement = useCurrentEngagement();
  const waiting = useWaitingCount();

  return (
    <box flexDirection="row" gap={1}>
      
      <text fg={mode === Mode.PLAN ? colors.planMode : colors.primary}>
        {mode === Mode.PLAN ? "Plan" : "Build"}
      </text>

      <text attributes={TextAttributes.DIM} fg={colors.dimSeparator}>
        ›
      </text>
      <text>{model}</text>
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
