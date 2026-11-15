import { useCallback, useEffect, useState } from "react";
import { TextAttributes } from "@opentui/core";
import { useDialog } from "../../providers/dialog";
import { useToast } from "../../providers/toast";
import { cerberus } from "../../lib/cerberus/api";
import { DialogSearchList } from "../dialog-search-list";

export type KeyStatus = { provider: string; hasKey: boolean };

export type KeysIo = {
  list: () => Promise<KeyStatus[]>;
  remove: (provider: string) => Promise<unknown>;
};

const realIo: KeysIo = { list: () => cerberus.apiKeys(), remove: (provider) => cerberus.removeApiKey(provider) };

const NAMES: Record<string, string> = { anthropic: "Claude (Anthropic)", openai: "GPT (OpenAI)", deepseek: "DeepSeek" };

/**
 * "/keys": which API providers have a key stored for this operator. A key is never typed here, where it
 * would be on the screen: it is set from a terminal with `cerberus key set <provider>`, which reads it
 * from the environment or asks for it without showing it. Enter removes the highlighted provider's key.
 */
export const KeysDialogContent = ({ io = realIo }: { io?: KeysIo }) => {
  const [keys, setKeys] = useState<KeyStatus[] | null>(null);
  const { close } = useDialog();
  const { show } = useToast();

  const reload = useCallback(async () => setKeys(await io.list()), [io]);

  useEffect(() => {
    reload().catch((problem: unknown) => {
      show({ variant: "error", message: problem instanceof Error ? problem.message : "Could not load the keys" });
      close();
    });
  }, [reload, close, show]);

  if (!keys) return <text attributes={TextAttributes.DIM}>Loading keys...</text>;

  return (
    <DialogSearchList
      items={keys}
      onSelect={(key) => {
        if (!key.hasKey) {
          show({ variant: "info", message: `No key is stored for ${NAMES[key.provider] ?? key.provider}. From a terminal: cerberus key set ${key.provider}` });
          return;
        }
        io.remove(key.provider)
          .then(async () => {
            await reload();
            show({ variant: "info", message: `The ${NAMES[key.provider] ?? key.provider} key was removed.` });
          })
          .catch((problem: unknown) => show({ variant: "error", message: problem instanceof Error ? problem.message : "Could not remove that key" }));
      }}
      filterFn={(key, query) => `${key.provider} ${NAMES[key.provider] ?? ""}`.toLowerCase().includes(query.toLowerCase())}
      renderItem={(key, isSelected) => (
        <>
          <text selectable={false} fg={isSelected ? "black" : "white"}>
            {NAMES[key.provider] ?? key.provider}
          </text>
          <box flexGrow={1} />
          <text selectable={false} fg={isSelected ? "black" : undefined} attributes={TextAttributes.DIM}>
            {key.hasKey ? "key stored · enter removes it" : `no key · cerberus key set ${key.provider}`}
          </text>
        </>
      )}
      getKey={(key) => key.provider}
      placeholder="Keys are set from a terminal: cerberus key set <provider>"
      emptyText="No such provider"
    />
  );
};
