// How "/undo" and "/redo" reach the conversation they are about. The command
// only asks; the chat screen that is open on that conversation carries it out,
// because only it knows whether the agent is at rest and can tell the agent.

export type UndoKind = "undo" | "redo";

const listeners = new Map<string, Set<(kind: UndoKind) => void>>();

/** Called by the chat screen of a conversation; returns how to stop listening. */
export function onUndoRequest(sessionId: string, listener: (kind: UndoKind) => void): () => void {
  if (!listeners.has(sessionId)) listeners.set(sessionId, new Set());
  listeners.get(sessionId)!.add(listener);
  return () => {
    listeners.get(sessionId)?.delete(listener);
  };
}

/** Asks the open conversation to undo or redo. False when no screen is open on it. */
export function requestUndo(sessionId: string, kind: UndoKind): boolean {
  const open = listeners.get(sessionId);
  if (!open || open.size === 0) return false;
  for (const listener of open) listener(kind);
  return true;
}
