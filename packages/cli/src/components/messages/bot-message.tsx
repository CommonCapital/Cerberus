import prettyMs from "pretty-ms";
import { EmptyBorder } from "../border";
import { useTheme } from "../../providers/theme";
import type { Message } from "../../hooks/use-chat";
import { Mode, type ModeType } from "@nightcode/shared";
import { TextAttributes } from "@opentui/core";
import { summarizeToolCall, toolResultLines } from "../../lib/tool-display";
import { foldQuietRuns, type Shown } from "../../lib/tool-folding";
import { useToolCallsExpanded } from "../../lib/tool-fold-state";
import { markdownStyle } from "../../lib/markdown-style";
import { useHelperProgress } from "../../lib/subagent-progress";

type ClientMessagePart = Message["parts"][number];
type ToolPart = Extract<ClientMessagePart, { type: `tool-${string}` | "dynamic-tool" }>;

type Props = {
  parts: ClientMessagePart[];
  model: string;
  mode: ModeType;
  durationMs?: number;
  streaming?: boolean;
};

function formatToolName(name: string): string {
  return name
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/^./, (c) => c.toUpperCase());
};

function isToolPart(part: ClientMessagePart): part is ToolPart {
  return part.type === "dynamic-tool" || part.type.startsWith("tool-");
};

function formatToolArgs(tc: ToolPart, toolName: string): string {
  if (!("input" in tc) || tc.input == null) return "";
  return summarizeToolCall(toolName, tc.input);
}

/** What a helper is doing right now, under the call that started it. */
function HelperProgress({ toolCallId }: { toolCallId: string }) {
  const progress = useHelperProgress(toolCallId);
  return progress ? <text attributes={TextAttributes.DIM}>{"  "}↳ {progress}</text> : null;
}

type Item = Shown<ClientMessagePart>;

type ItemGroup = {
  type: string;
  items: Item[];
  key: string;
};

/** What is shown, in groups of the same kind; a blank line separates one group from the next. */
function groupConsecutive(items: Item[]): ItemGroup[] {
  const groups: ItemGroup[] = [];

  for (const item of items) {
    const type = item.kind === "fold" ? "fold" : item.part.type;
    const lastGroup = groups[groups.length - 1];

    if (lastGroup && lastGroup.type === type) {
      lastGroup.items.push(item);
    } else {
      const key =
        item.kind === "fold"
          ? `group-${item.key}`
          : isToolPart(item.part)
            ? `group-tc-${item.part.toolCallId}`
            : `group-${type}-${item.index}`;
      groups.push({ type, items: [item], key });
    }
  }

  return groups;
};

export function BotMessage({ 
  parts,
  model,
  mode,
  durationMs,
  streaming = false,
}: Props) {
  const { colors } = useTheme();
  const expanded = useToolCallsExpanded();
  const style = markdownStyle(colors);
  const lastIndex = parts.length - 1;
  return (
    <box width="100%" alignItems="center">
      {groupConsecutive(foldQuietRuns(parts, expanded)).map((group, i) => (
        <box key={group.key} width="100%" paddingTop={i === 0 ? 0 : 1}>
          {group.items.map((item) => {
            if (item.kind === "fold") {
              return (
                <box
                  key={item.key}
                  border={["left"]}
                  borderColor={colors.thinkingBorder}
                  customBorderChars={{
                    ...EmptyBorder,
                    vertical: "│",
                  }}
                  width="100%"
                  paddingX={2}
                >
                  <text attributes={TextAttributes.DIM}>
                    <em fg={colors.info}>Explored:</em> {item.summary} · ctrl+o shows each
                  </text>
                </box>
              );
            }

            const part = item.part;
            const j = item.index;
            if (part.type === "reasoning") {
              return (
                <box
                  key={`reasoning-${j}`}
                  border={["left"]}
                  borderColor={colors.thinkingBorder}
                  customBorderChars={{
                    ...EmptyBorder,
                    vertical: "│",
                  }}
                  width="100%"
                  paddingX={2}
                >
                  <text attributes={TextAttributes.DIM}>
                    <em fg={colors.thinking}>Thinking:</em> {part.text}
                  </text>
                </box>
              );
            }

            if (isToolPart(part)) {
              const toolName =
                part.type === "dynamic-tool" ? part.toolName : part.type.slice("tool-".length);

              return (
                <box
                  key={part.toolCallId}
                  border={["left"]}
                  borderColor={colors.thinkingBorder}
                  customBorderChars={{
                    ...EmptyBorder,
                    vertical: "│",
                  }}
                  width="100%"
                  paddingX={2}
                >
                  <text attributes={TextAttributes.DIM}>
                    <em fg={colors.info}>{formatToolName(toolName)}:</em> {formatToolArgs(part, toolName)}
                    {part.state !== "output-available" && part.state !== "output-error" 
                      ? " …" 
                      : ""
                    }
                    {part.state === "output-error" ? ` ${part.errorText}` : ""}
                  </text>
                  {toolName === "task" && part.state !== "output-available" && part.state !== "output-error" ? (
                    <HelperProgress toolCallId={part.toolCallId} />
                  ) : null}
                  {/* What it did: the change an edit made, how a command ended. */}
                  {part.state === "output-available"
                    ? toolResultLines(toolName, part.output).map((line, k) => (
                        <text
                          key={k}
                          fg={line.tone === "added" ? colors.success : line.tone === "removed" || line.tone === "problem" ? colors.error : undefined}
                          attributes={line.tone === "plain" ? TextAttributes.DIM : 0}
                        >
                          {"  "}
                          {line.text}
                        </text>
                      ))
                    : null}
                </box>
              );
            }

            if (part.type === "text") {
              return (
                <box key={`text-${j}`} paddingX={3} width="100%">
                  {/* The model writes markdown. While its last block is still arriving it is not final. */}
                  <markdown content={part.text} syntaxStyle={style} streaming={streaming && j === lastIndex} />
                </box>
              );
            }
            
            return null;
          })}
        </box>
      ))}

      <box paddingX={3} paddingY={1} gap={1} width="100%">
        <box flexDirection="row" gap={2}>
          <text fg={mode === Mode.PLAN ? colors.planMode : colors.primary}>◉</text>
          <box flexDirection="row" gap={1}>
            <text>
              {mode === Mode.PLAN ? "Plan" : "Build"}
            </text>
            <text attributes={TextAttributes.DIM} fg={colors.dimSeparator}>
              ›
            </text>
            <text attributes={TextAttributes.DIM}>{model}</text>
            {(durationMs != null) && (
              <>
                <text attributes={TextAttributes.DIM} fg={colors.dimSeparator}>
                  ›
                </text>
                <text attributes={TextAttributes.DIM}>
                  {prettyMs(durationMs)}
                </text>
              </>
            )}
          </box>
        </box>
      </box>
    </box>
  );
};
