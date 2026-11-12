import { TextAttributes } from "@opentui/core";
import { EmptyBorder } from "../border";
import { useTheme } from "../../providers/theme";
import type { ShellRecord } from "../../lib/chat-flow";

const MAX_LINES = 12;

/** A command the operator ran themself with "!": the command, the end of what it printed, and how it ended. */
export function ShellMessage({ record }: { record: ShellRecord }) {
  const { colors } = useTheme();
  const lines = record.output.length > 0 ? record.output.split("\n") : [];
  const shown = lines.slice(-MAX_LINES);
  const failed = record.timedOut || record.exitCode !== 0;

  return (
    <box width="100%" alignItems="center">
      <box
        border={["left"]}
        borderColor={failed ? colors.error : colors.dimSeparator}
        width="100%"
        customBorderChars={{ ...EmptyBorder, vertical: "┃", bottomLeft: "╹" }}
      >
        <box flexDirection="column" paddingX={2} paddingY={1} backgroundColor={colors.surface} width="100%">
          <text attributes={TextAttributes.BOLD}>$ {record.command}</text>
          {lines.length > shown.length ? (
            <text attributes={TextAttributes.DIM}>… {lines.length - shown.length} earlier line(s)</text>
          ) : null}
          {shown.map((line, index) => (
            <text key={index} attributes={TextAttributes.DIM}>
              {line.length > 0 ? line : " "}
            </text>
          ))}
          {failed ? (
            <text fg={colors.error}>{record.timedOut ? "stopped: it ran too long" : `exit ${record.exitCode}`}</text>
          ) : null}
        </box>
      </box>
    </box>
  );
}
