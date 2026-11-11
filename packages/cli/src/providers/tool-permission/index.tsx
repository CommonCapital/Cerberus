import { createContext, useCallback, useContext, useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import { TextAttributes } from "@opentui/core";
import { useKeyboard } from "@opentui/react";
import { useKeyboardLayer } from "../keyboard-layer";
import { useTheme } from "../theme";
import { describeToolCall, KIND_LABEL, kindOfTool } from "../../lib/cerberus/tool-permissions";

// Human in the loop for chat: before the CLI runs a tool that needs it, the
// operator is shown what will happen and decides — once, always, or no.

export type ToolDecision = "allow" | "always" | "deny";

export type ToolRequest = { toolName: string; input: unknown };

type Pending = ToolRequest & { resolve: (decision: ToolDecision) => void };

type ToolPermissionContextValue = {
  /** Asks the operator about one tool call. Requests are shown one at a time, in order. */
  ask: (request: ToolRequest) => Promise<ToolDecision>;
  /** The request on screen, if any. */
  current: ToolRequest | null;
  /** How many more are waiting behind it. */
  waiting: number;
  decide: (decision: ToolDecision) => void;
  /** Denies everything outstanding (the reply was interrupted or the screen closed). */
  denyAll: () => void;
};

const ToolPermissionContext = createContext<ToolPermissionContextValue | null>(null);

export function useToolPermission(): ToolPermissionContextValue {
  const value = useContext(ToolPermissionContext);
  if (!value) throw new Error("useToolPermission must be used within a ToolPermissionProvider");
  return value;
}

const LAYER = "permission";

export function ToolPermissionProvider({ children }: { children: ReactNode }) {
  const [queue, setQueue] = useState<Pending[]>([]);
  const queueRef = useRef(queue);
  queueRef.current = queue;
  const { push, pop } = useKeyboardLayer();

  const decide = useCallback((decision: ToolDecision) => {
    const [first, ...rest] = queueRef.current;
    if (!first) return;
    first.resolve(decision);

    // "Always" answers every waiting request of the same kind too — that is what it means.
    if (decision === "always") {
      const kind = kindOfTool(first.toolName);
      const remaining = rest.filter((pending) => {
        if (kindOfTool(pending.toolName) !== kind) return true;
        pending.resolve("allow");
        return false;
      });
      queueRef.current = remaining;
      setQueue(remaining);
    } else {
      queueRef.current = rest;
      setQueue(rest);
    }
  }, []);

  const denyAll = useCallback(() => {
    for (const pending of queueRef.current) pending.resolve("deny");
    queueRef.current = [];
    setQueue([]);
  }, []);

  const ask = useCallback(
    (request: ToolRequest) =>
      new Promise<ToolDecision>((resolve) => {
        const next = [...queueRef.current, { ...request, resolve }];
        queueRef.current = next;
        setQueue(next);
      }),
    [],
  );

  // While a request is on screen it owns the keyboard: the input bar loses focus,
  // and Ctrl+C denies instead of quitting.
  const hasPending = queue.length > 0;
  useEffect(() => {
    if (!hasPending) return;
    push(LAYER, () => {
      denyAll();
      return true;
    });
    return () => pop(LAYER);
  }, [hasPending, push, pop, denyAll]);

  const first = queue[0];
  const value: ToolPermissionContextValue = {
    ask,
    current: first ? { toolName: first.toolName, input: first.input } : null,
    waiting: Math.max(0, queue.length - 1),
    decide,
    denyAll,
  };

  return <ToolPermissionContext.Provider value={value}>{children}</ToolPermissionContext.Provider>;
}

/** The prompt itself. Renders nothing when no tool call is waiting for a decision. */
export function ToolPermissionPrompt() {
  const { current, waiting, decide } = useToolPermission();
  const { isTopLayer } = useKeyboardLayer();
  const { colors } = useTheme();

  useKeyboard((key) => {
    if (!current || !isTopLayer(LAYER)) return;

    if (key.name === "y") decide("allow");
    else if (key.name === "a") decide("always");
    else if (key.name === "n" || key.name === "escape") decide("deny");
  });

  if (!current) return null;

  const { title, detail } = describeToolCall(current.toolName, current.input);
  const kind = KIND_LABEL[kindOfTool(current.toolName)];

  return (
    <box flexDirection="column" border borderColor={colors.planMode} paddingX={1} flexShrink={0}>
      <text fg={colors.planMode} attributes={TextAttributes.BOLD}>
        Allow this?
      </text>
      <text>{title}</text>
      {detail
        ? detail.split("\n").map((line, index) => (
            <text key={index} attributes={TextAttributes.DIM}>
              {line}
            </text>
          ))
        : null}
      <box flexDirection="row" gap={1} marginTop={1}>
        <text fg={colors.success}>y</text>
        <text attributes={TextAttributes.DIM}>yes, once</text>
        <text fg={colors.success}>a</text>
        <text attributes={TextAttributes.DIM}>always allow {kind}</text>
        <text fg={colors.error}>n</text>
        <text attributes={TextAttributes.DIM}>no</text>
        {waiting > 0 ? <text attributes={TextAttributes.DIM}>· {waiting} more waiting</text> : null}
      </box>
    </box>
  );
}
