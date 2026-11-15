import { useRef, useState, type ReactNode } from "react";
import { TextAttributes, type InputRenderable } from "@opentui/core";
import { useKeyboard } from "@opentui/react";
import { useKeyboardLayer } from "../providers/keyboard-layer";
import { useTheme } from "../providers/theme";

const MAX_VISIBLE = 8;

export type DraftAction = { key: string; label: string; hint?: string };

type Props<T> = {
  items: T[];
  getKey: (item: T) => string;
  renderItem: (item: T, isSelected: boolean) => ReactNode;
  /** Enter on an item, with nothing typed. */
  onItem: (item: T) => void | Promise<void>;
  /** What can be done with typed text; shown instead of the items while there is some. */
  draftActions: (text: string) => DraftAction[];
  /** Enter on one of those. The field is cleared when this resolves. */
  onDraft: (action: DraftAction, text: string) => void | Promise<void>;
  placeholder: string;
  emptyText: string;
  /** The line under the list when nothing is typed. */
  hint: string;
};

/**
 * A dialog body with one text field and a list: with nothing typed, the list
 * shows existing items and Enter acts on the highlighted one; while typing,
 * it shows what can be done with the text.
 */
export function DialogInputList<T>({
  items, getKey, renderItem, onItem, draftActions, onDraft, placeholder, emptyText, hint,
}: Props<T>) {
  const [draft, setDraft] = useState("");
  const [selected, setSelected] = useState(0);
  const inputRef = useRef<InputRenderable>(null);
  const busy = useRef(false);
  const { isTopLayer } = useKeyboardLayer();
  const { colors } = useTheme();

  const text = draft.trim();
  const actions = text ? draftActions(text) : [];
  const rowCount = text ? actions.length : items.length;
  const index = Math.max(0, Math.min(selected, rowCount - 1));

  useKeyboard((key) => {
    if (!isTopLayer("dialog")) return;

    if (key.name === "up") setSelected(Math.max(0, index - 1));
    else if (key.name === "down") setSelected(Math.min(rowCount - 1, index + 1));
    else if ((key.name === "return" || key.name === "enter") && !busy.current) {
      const run = async () => {
        if (text) {
          const action = actions[index];
          if (!action) return;
          await onDraft(action, text);
          if (inputRef.current) inputRef.current.value = "";
          setDraft("");
          setSelected(0);
        } else {
          const item = items[index];
          if (item) await onItem(item);
        }
      };
      busy.current = true;
      // The callbacks report their own failures; a rejected one only leaves the field as it was.
      run().catch(() => {}).finally(() => {
        busy.current = false;
      });
    }
  });

  const first = Math.max(0, Math.min(index - MAX_VISIBLE + 1, items.length - MAX_VISIBLE));

  return (
    <box flexDirection="column" gap={1}>
      <input
        ref={inputRef}
        placeholder={placeholder}
        focused
        onContentChange={() => {
          setDraft(inputRef.current?.value ?? "");
          setSelected(0);
        }}
      />
      {text ? (
        <box flexDirection="column">
          {actions.map((action, i) => (
            <box key={action.key} flexDirection="row" gap={1} height={1} overflow="hidden" backgroundColor={i === index ? colors.selection : undefined}>
              <text selectable={false} fg={i === index ? "black" : "white"}>
                {action.label}
              </text>
              <box flexGrow={1} />
              {action.hint ? (
                <text selectable={false} fg={i === index ? "black" : undefined} attributes={TextAttributes.DIM}>
                  {action.hint}
                </text>
              ) : null}
            </box>
          ))}
        </box>
      ) : items.length === 0 ? (
        <text attributes={TextAttributes.DIM}>{emptyText}</text>
      ) : (
        <box flexDirection="column">
          {items.slice(first, first + MAX_VISIBLE).map((item, offset) => (
            <box
              key={getKey(item)}
              flexDirection="row"
              gap={1}
              height={1}
              overflow="hidden"
              backgroundColor={first + offset === index ? colors.selection : undefined}
            >
              {renderItem(item, first + offset === index)}
            </box>
          ))}
          {items.length > MAX_VISIBLE ? (
            <text attributes={TextAttributes.DIM}>
              {index + 1} of {items.length}
            </text>
          ) : null}
        </box>
      )}
      <text attributes={TextAttributes.DIM}>{text ? "↑↓ choose · enter confirm" : hint}</text>
    </box>
  );
}
