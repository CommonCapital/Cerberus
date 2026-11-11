import { TextAttributes } from "@opentui/core";
import { useTheme } from "../providers/theme";
import { usePromptConfig } from "../providers/prompt-config";
import { Mode } from "@nightcode/shared";
import { useCurrentEngagement } from "../lib/cerberus/engagement";

export function StatusBar() {
  const { mode, model } = usePromptConfig();
  const { colors } = useTheme();
  const engagement = useCurrentEngagement();

  return (
    <box flexDirection="row" gap={1}>
      
      <text fg={mode === Mode.PLAN ? colors.planMode : colors.primary}>
        {mode === Mode.PLAN ? "Plan" : "Build"}
      </text>

      <text attributes={TextAttributes.DIM} fg={colors.dimSeparator}>
        ›
      </text>
      <text>{model}</text>
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
