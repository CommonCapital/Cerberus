import { useCallback, useEffect, useRef, useState } from "react";
import { TextAttributes, type InputRenderable } from "@opentui/core";
import { useKeyboard } from "@opentui/react";
import { useDialog } from "../../providers/dialog";
import { useKeyboardLayer } from "../../providers/keyboard-layer";
import { useTheme } from "../../providers/theme";
import { useToast } from "../../providers/toast";
import { cerberus, type MemoryEntry } from "../../lib/cerberus/api";

const MAX_VISIBLE = 8;

const ADD = [
  { kind: "note" as const, label: "Save as a note", hint: "context for agents" },
  { kind: "instruction" as const, label: "Save as an instruction", hint: "always followed" },
];

/** One line for an entry: who wrote it, what kind, about which target. */
export function describeEntry(entry: MemoryEntry): string {
  const what = entry.kind === "instruction" ? "instruction" : entry.source === "agent" ? "note by a model" : "note";
  return `${what}${entry.targetPath ? ` · ${entry.targetPath}` : ""}`;
}

/**
 * What Cerberus remembers between runs: the operator's standing instructions
 * and notes, and notes agents left. Type to add one; with nothing typed,
 * enter forgets the highlighted entry.
 */
export const MemoryDialogContent = () => {
  const [entries, setEntries] = useState<MemoryEntry[] | null>(null);
  const [draft, setDraft] = useState("");
  const [selected, setSelected] = useState(0);
  const inputRef = useRef<InputRenderable>(null);
  const busy = useRef(false);
  const { close } = useDialog();
  const { isTopLayer } = useKeyboardLayer();
  const { colors } = useTheme();
  const { show } = useToast();

  const fail = useCallback(
    (problem: unknown, fallback: string) =>
      show({ variant: "error", message: problem instanceof Error ? problem.message : fallback }),
    [show],
  );

  useEffect(() => {
    let ignore = false;
    cerberus
      .memory()
      .then((result) => {
        if (!ignore) setEntries(result);
      })
      .catch((problem: unknown) => {
        if (ignore) return;
        fail(problem, "Could not load the memory");
        close();
      });
    return () => {
      ignore = true;
    };
  }, [close, fail]);

  const adding = draft.trim().length > 0;
  const rowCount = adding ? ADD.length : (entries?.length ?? 0);

  useKeyboard((key) => {
    if (!isTopLayer("dialog") || !entries) return;

    if (key.name === "up") setSelected((index) => Math.max(0, index - 1));
    else if (key.name === "down") setSelected((index) => Math.min(rowCount - 1, index + 1));
    else if ((key.name === "return" || key.name === "enter") && !busy.current) {
      busy.current = true;
      const done = () => {
        busy.current = false;
      };

      if (adding) {
        const choice = ADD[selected] ?? ADD[0]!;
        cerberus
          .remember(choice.kind, draft.trim())
          .then((entry) => {
            setEntries((current) => [...(current ?? []), entry]);
            if (inputRef.current) inputRef.current.value = "";
            setDraft("");
            setSelected(0);
          })
          .catch((problem: unknown) => fail(problem, "Could not save that"))
          .finally(done);
        return;
      }

      const entry = entries[selected];
      if (!entry) {
        done();
        return;
      }
      cerberus
        .forget(entry.id)
        .then(() => {
          setEntries((current) => (current ?? []).filter((e) => e.id !== entry.id));
          setSelected((index) => Math.max(0, Math.min(index, entries.length - 2)));
          show({ variant: "info", message: `Forgotten: ${entry.text.slice(0, 60)}` });
        })
        .catch((problem: unknown) => fail(problem, "Could not forget that"))
        .finally(done);
    }
  });

  if (!entries) {
    return <text attributes={TextAttributes.DIM}>Loading the memory...</text>;
  }

  // Keep the highlighted entry in view.
  const first = adding ? 0 : Math.max(0, Math.min(selected - MAX_VISIBLE + 1, entries.length - MAX_VISIBLE));
  const visible = entries.slice(first, first + MAX_VISIBLE);

  return (
    <box flexDirection="column" gap={1}>
      <input
        ref={inputRef}
        placeholder="Type something to remember"
        focused
        onContentChange={() => {
          setDraft(inputRef.current?.value ?? "");
          setSelected(0);
        }}
      />
      {adding ? (
        <box flexDirection="column">
          {ADD.map((choice, index) => (
            <box key={choice.kind} flexDirection="row" height={1} backgroundColor={index === selected ? colors.selection : undefined}>
              <text selectable={false} fg={index === selected ? "black" : "white"}>
                {choice.label}
              </text>
              <box flexGrow={1} />
              <text selectable={false} fg={index === selected ? "black" : undefined} attributes={TextAttributes.DIM}>
                {choice.hint}
              </text>
            </box>
          ))}
        </box>
      ) : entries.length === 0 ? (
        <text attributes={TextAttributes.DIM}>Nothing is remembered yet.</text>
      ) : (
        <box flexDirection="column">
          {visible.map((entry, offset) => {
            const isSelected = first + offset === selected;
            return (
              <box key={entry.id} flexDirection="row" gap={1} height={1} overflow="hidden" backgroundColor={isSelected ? colors.selection : undefined}>
                <text selectable={false} fg={isSelected ? "black" : "white"}>
                  {entry.text}
                </text>
                <box flexGrow={1} />
                <text selectable={false} fg={isSelected ? "black" : undefined} attributes={TextAttributes.DIM}>
                  {describeEntry(entry)}
                </text>
              </box>
            );
          })}
          {entries.length > MAX_VISIBLE ? (
            <text attributes={TextAttributes.DIM}>
              {selected + 1} of {entries.length}
            </text>
          ) : null}
        </box>
      )}
      <text attributes={TextAttributes.DIM}>
        {adding ? "↑↓ choose · enter save" : entries.length > 0 ? "↑↓ select · enter forget the highlighted entry" : "enter saves what you type"}
      </text>
    </box>
  );
};
