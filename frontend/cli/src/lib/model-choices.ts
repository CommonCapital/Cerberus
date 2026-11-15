// What the model picker offers, from the server's listing: every provider's
// models, whether each can be used right now, and — since the list is an
// offer and not a limit — whether something typed can be used as a model id.

export type ProviderListing = {
  id: string;
  name: string;
  kind: "api" | "local";
  ready: boolean;
  keySource?: "yours" | "server" | null;
  reachable?: boolean;
  models: string[];
  anyModel?: string;
};

export type ModelListing = { default: string; providers: ProviderListing[] };

/** @param note Short enough for a row of the picker. @param help What to do before it can be used, when it cannot yet. */
export type ModelChoice = { id: string; note: string; ready: boolean; help?: string };

function describe(provider: ProviderListing): { note: string; help?: string } {
  if (provider.id === "dev") return { note: "test model" };
  if (provider.kind === "local") {
    if (provider.reachable === false) return { note: "local · not answering", help: "Ollama is not answering. Start it, then pull the model: ollama pull <model>" };
    return { note: "local · installed" };
  }
  if (!provider.ready) return { note: "API · needs a key", help: `It needs a key first. From a terminal: cerberus key set ${provider.id}` };
  return { note: provider.keySource === "server" ? "API · server key" : "API · your key" };
}

/** Every listed model, ready ones first within the order the server gave. */
export function modelChoices(listing: ModelListing): ModelChoice[] {
  const all = listing.providers.flatMap((provider) =>
    provider.models.map((id) => ({ id, ...describe(provider), ready: provider.ready || provider.id === "dev" })),
  );
  return [...all.filter((choice) => choice.ready), ...all.filter((choice) => !choice.ready)];
}

const PREFIXED = /^(anthropic|openai|deepseek|ollama):[A-Za-z0-9._:/@+-]+$/i;
const FAMILY = /^(claude-|gpt-|chatgpt-|o\d)[A-Za-z0-9._:/@+-]*$/;

/**
 * Whether typed text can be sent as a model id: `provider:model`, or a name of a family that belongs to one
 * provider. The server has the same rule and the last word; this only keeps a slip of the hand from being chosen.
 */
export const looksLikeModelId = (text: string) => PREFIXED.test(text) || FAMILY.test(text);

export const HOW_TO_NAME_ANY_MODEL = "Or type any model: ollama:name, openai:name";

/** What typing offers: the listed models that match, and the text itself when it can be a model id. */
export function draftChoices(text: string, choices: ModelChoice[]): { key: string; label: string; hint?: string }[] {
  const query = text.toLowerCase();
  const matching = choices.filter((choice) => choice.id.toLowerCase().includes(query)).slice(0, 6);
  const offers = matching.map((choice) => ({ key: `pick:${choice.id}`, label: choice.id, hint: choice.note }));
  if (looksLikeModelId(text) && !choices.some((choice) => choice.id === text)) {
    offers.push({ key: "typed", label: `Use ${text}`, hint: "as typed" });
  }
  return offers;
}
