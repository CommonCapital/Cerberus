import { useSyncExternalStore } from "react";

// Whether folded runs of tool calls are shown in full. One switch for the
// whole chat (ctrl+o), kept outside the components so every reply follows it.

let expanded = false;
const listeners = new Set<() => void>();

export const toolCallsExpanded = () => expanded;

export function setToolCallsExpanded(next: boolean) {
  if (next === expanded) return;
  expanded = next;
  for (const listener of listeners) listener();
}

export const toggleToolCalls = () => setToolCallsExpanded(!expanded);

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export const useToolCallsExpanded = () => useSyncExternalStore(subscribe, toolCallsExpanded, toolCallsExpanded);
