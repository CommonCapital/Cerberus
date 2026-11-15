export type ModelPricing = {
  inputUsdPerMillionTokens: number;
  outputUsdPerMillionTokens: number;
};

export type SupportedProvider = "anthropic" | "openai" | "ollama";

type SupportedChatModelDefinition = {
  id: string;
  provider: SupportedProvider;
  pricing: ModelPricing;
};

export const SUPPORTED_CHAT_MODELS = [
  {
    id: "claude-opus-5-5",
    provider: "anthropic",
    pricing: {
      inputUsdPerMillionTokens: 4,
      outputUsdPerMillionTokens: 20,
    },
  },
  {
    id: "claude-sonnet-5-5",
    provider: "anthropic",
    pricing: {
      inputUsdPerMillionTokens: 2,
      outputUsdPerMillionTokens: 10,
    },
  },
  {
    id: "claude-haiku-5-5",
    provider: "anthropic",
    pricing: {
      inputUsdPerMillionTokens: 0.1,
      outputUsdPerMillionTokens: 0.5,
    },
  },
  {
    id: "claude-fable-5-2",
    provider: "anthropic",
    pricing: {
      inputUsdPerMillionTokens: 8,
      outputUsdPerMillionTokens: 40,
    },
  },
  {
    id: "gpt-5.4",
    provider: "openai",
    pricing: {
      inputUsdPerMillionTokens: 2.5,
      outputUsdPerMillionTokens: 15,
    },
  },
  {
    id: "gpt-5.4-mini",
    provider: "openai",
    pricing: {
      inputUsdPerMillionTokens: 0.75,
      outputUsdPerMillionTokens: 4.5,
    },
  },
  {
    id: "gpt-5.4-nano",
    provider: "openai",
    pricing: {
      inputUsdPerMillionTokens: 0.2,
      outputUsdPerMillionTokens: 1.25,
    },
  },
  // Local models served by Ollama (http://localhost:11434). Free — they run on
  // the user's own machine. Pull them first, e.g. `ollama pull llama3.1`.
  {
    id: "llama3.1",
    provider: "ollama",
    pricing: { inputUsdPerMillionTokens: 0, outputUsdPerMillionTokens: 0 },
  },
  {
    id: "qwen2.5-coder",
    provider: "ollama",
    pricing: { inputUsdPerMillionTokens: 0, outputUsdPerMillionTokens: 0 },
  },
  {
    id: "deepseek-r1",
    provider: "ollama",
    pricing: { inputUsdPerMillionTokens: 0, outputUsdPerMillionTokens: 0 },
  },
] as const satisfies readonly SupportedChatModelDefinition[];

export type SupportedChatModel = (typeof SUPPORTED_CHAT_MODELS)[number];
/**
 * A model id. Any model of a supported provider can be chosen — a listed id, or `provider:model`
 * (`anthropic:`, `openai:`, `deepseek:`, `ollama:`) — so this is a string, not the list above.
 * The server decides what it accepts and says what is installed and ready (`GET /models`).
 */
export type SupportedChatModelId = string;

export function findSupportedChatModel(modelId: string) {
  return SUPPORTED_CHAT_MODELS.find((model) => model.id === modelId);
}

export const DEFAULT_CHAT_MODEL_ID: SupportedChatModelId = "claude-opus-5-5";
