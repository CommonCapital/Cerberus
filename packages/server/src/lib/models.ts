import { createAnthropic } from "@ai-sdk/anthropic";
import { createOpenAI } from "@ai-sdk/openai";
import {
  findSupportedChatModel,
  type SupportedChatModel,
  type SupportedChatModelId,
  type SupportedProvider,
} from "@cerberus/shared";
import type { ProviderOptions } from "@ai-sdk/provider-utils";
import type { LanguageModel } from "ai";

type AnthropicModelId = Extract<SupportedChatModel, { provider: "anthropic" }>["id"];
type OpenAIModelId = Extract<SupportedChatModel, { provider: "openai" }>["id"];
type OllamaModelId = Extract<SupportedChatModel, { provider: "ollama" }>["id"];

export type ResolvedModel = {
  model: LanguageModel;
  provider: SupportedProvider;
  modelId: SupportedChatModelId;
  providerOptions?: ProviderOptions;
};

/**
 * Per-request credentials. Keys are supplied by the user (never baked in):
 * from the request, or falling back to local environment variables. Ollama
 * needs no key — it runs locally.
 */
export type ModelCredentials = {
  anthropicApiKey?: string;
  openaiApiKey?: string;
  ollamaBaseUrl?: string;
};

// The current Claude models think adaptively when nothing is said about thinking, and reject a
// fixed thinking budget — so no thinking option is sent for any of them.
const ANTHROPIC_PROVIDER_OPTIONS: Partial<Record<AnthropicModelId, ProviderOptions>> = {};

const OPENAI_PROVIDER_OPTIONS: Partial<Record<OpenAIModelId, ProviderOptions>> = {
  "gpt-5.4": {
    openai: { thinking: { reasoningSummary: "detailed" } },
  },
};

function assertUnsupportedProvider(provider: never): never {
  throw new Error(`Unsupported provider: ${provider}`);
}

function resolveAnthropicModel(modelId: AnthropicModelId, creds: ModelCredentials): ResolvedModel {
  const apiKey = creds.anthropicApiKey ?? process.env.ANTHROPIC_API_KEY;
  if (!apiKey) {
    throw new Error("No Anthropic API key. Set ANTHROPIC_API_KEY or use a local Ollama model.");
  }
  const anthropic = createAnthropic({ apiKey });
  return {
    model: anthropic(modelId),
    provider: "anthropic",
    modelId,
    providerOptions: ANTHROPIC_PROVIDER_OPTIONS[modelId],
  };
}

function resolveOpenAIModel(modelId: OpenAIModelId, creds: ModelCredentials): ResolvedModel {
  const apiKey = creds.openaiApiKey ?? process.env.OPENAI_API_KEY;
  if (!apiKey) {
    throw new Error("No OpenAI API key. Set OPENAI_API_KEY or use a local Ollama model.");
  }
  const openai = createOpenAI({ apiKey });
  return {
    model: openai(modelId),
    provider: "openai",
    modelId,
    providerOptions: OPENAI_PROVIDER_OPTIONS[modelId],
  };
}

function resolveOllamaModel(modelId: OllamaModelId, creds: ModelCredentials): ResolvedModel {
  // Ollama exposes an OpenAI-compatible API at /v1 — no external key needed.
  const baseURL =
    creds.ollamaBaseUrl ?? process.env.OLLAMA_BASE_URL ?? "http://localhost:11434/v1";
  const ollama = createOpenAI({ baseURL, apiKey: "ollama", name: "ollama" });
  return {
    model: ollama(modelId),
    provider: "ollama",
    modelId,
  };
}

function resolveSupportedChatModel(model: SupportedChatModel, creds: ModelCredentials): ResolvedModel {
  const provider = model.provider;

  switch (provider) {
    case "anthropic":
      return resolveAnthropicModel(model.id, creds);
    case "openai":
      return resolveOpenAIModel(model.id, creds);
    case "ollama":
      return resolveOllamaModel(model.id, creds);
    default:
      return assertUnsupportedProvider(provider);
  }
}

export function isSupportedChatModel(modelId: string): modelId is SupportedChatModelId {
  return findSupportedChatModel(modelId) != null;
}

export function resolveChatModel(modelId: string, creds: ModelCredentials = {}): ResolvedModel {
  const model = findSupportedChatModel(modelId);
  if (!model) {
    throw new Error(`Unsupported model: ${modelId}`);
  }

  return resolveSupportedChatModel(model, creds);
}
