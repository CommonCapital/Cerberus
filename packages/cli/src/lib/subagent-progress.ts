import { useSyncExternalStore } from "react";

// What each running helper is doing right now, by the tool call that started
// it. Kept outside the components: the helper runs in the chat hook, and the
// line is shown by whichever message holds that call.

const progress = new Map<string, string>();
const listeners = new Set<() => void>();

export function setHelperProgress(toolCallId: string, text: string | null) {
  if (text === null) progress.delete(toolCallId);
  else progress.set(toolCallId, text);
  for (const listener of listeners) listener();
}

export const helperProgress = (toolCallId: string) => progress.get(toolCallId) ?? null;

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export const useHelperProgress = (toolCallId: string) =>
  useSyncExternalStore(subscribe, () => helperProgress(toolCallId), () => helperProgress(toolCallId));
