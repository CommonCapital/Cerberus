import { useEffect, useState } from "react";
import { TextAttributes } from "@opentui/core";
import { SUPPORTED_CHAT_MODELS } from "@cerberus/shared";
import { useDialog } from "../../providers/dialog";
import { useToast } from "../../providers/toast";
import { cerberus } from "../../lib/cerberus/api";
import { draftChoices, HOW_TO_NAME_ANY_MODEL, modelChoices, type ModelChoice, type ModelListing } from "../../lib/model-choices";
import { DialogInputList } from "../dialog-input-list";

type ModelsDialogContentProps = {
  onSelectModel: (modelId: string) => void;
  /** Where the list comes from; the server by default. */
  load?: () => Promise<ModelListing>;
};

/** What is offered when the server cannot say: the models this client knows by name, unchecked. */
const offline = (): ModelChoice[] =>
  SUPPORTED_CHAT_MODELS.map((model) => ({ id: model.id, note: model.provider === "ollama" ? "local" : "API", ready: true }));

/**
 * "/models": choose the model that answers — an API model (Claude, GPT, DeepSeek) or a local one served by
 * Ollama. The list says which can be used right now; typing narrows it, and any model of a provider can be
 * typed as `provider:model`.
 */
export const ModelsDialogContent = ({ onSelectModel, load = () => cerberus.models() }: ModelsDialogContentProps) => {
  const [choices, setChoices] = useState<ModelChoice[] | null>(null);
  const dialog = useDialog();
  const { show } = useToast();

  useEffect(() => {
    let ignore = false;
    load()
      .then((listing) => {
        if (!ignore) setChoices(modelChoices(listing));
      })
      .catch(() => {
        // An older server, or none reachable: the names are still worth offering.
        if (!ignore) setChoices(offline());
      });
    return () => {
      ignore = true;
    };
    // The loader is fixed for the life of the dialog.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const choose = (id: string) => {
    onSelectModel(id);
    dialog.close();
    // Chosen all the same: the operator may be about to add the key. But they are told what is missing.
    const help = choices?.find((choice) => choice.id === id)?.help;
    show({ variant: "info", message: help ? `Model: ${id}. ${help}` : `Model: ${id}` });
  };

  if (!choices) return <text attributes={TextAttributes.DIM}>Loading models...</text>;

  return (
    <DialogInputList
      items={choices}
      getKey={(choice) => choice.id}
      renderItem={(choice, isSelected) => (
        <>
          <text selectable={false} fg={isSelected ? "black" : "white"} attributes={choice.ready || isSelected ? 0 : TextAttributes.DIM}>
            {choice.id}
          </text>
          <box flexGrow={1} />
          <text selectable={false} fg={isSelected ? "black" : undefined} attributes={TextAttributes.DIM}>
            {choice.note}
          </text>
        </>
      )}
      onItem={(choice) => choose(choice.id)}
      draftActions={(text) => draftChoices(text, choices)}
      onDraft={(action, text) => {
        if (action.key === "typed") choose(text);
        else choose(action.key.slice("pick:".length));
      }}
      placeholder="Search, or type provider:model"
      emptyText="No models are listed."
      hint={HOW_TO_NAME_ANY_MODEL}
    />
  );
};
