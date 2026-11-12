import { expect, test } from "bun:test";
import { draftChoices, looksLikeModelId, modelChoices, type ModelListing } from "./model-choices";

const listing: ModelListing = {
  default: "claude-opus-5-5",
  providers: [
    { id: "anthropic", name: "Claude (Anthropic API)", kind: "api", ready: false, keySource: null, models: ["claude-opus-5-5", "claude-sonnet-5-5"], anyModel: "anthropic:<model>" },
    { id: "openai", name: "GPT (OpenAI API)", kind: "api", ready: true, keySource: "server", models: ["gpt-5.4"], anyModel: "openai:<model>" },
    { id: "deepseek", name: "DeepSeek (DeepSeek API)", kind: "api", ready: true, keySource: "yours", models: ["deepseek-chat"], anyModel: "deepseek:<model>" },
    { id: "ollama", name: "Ollama (local models)", kind: "local", ready: true, reachable: true, models: ["llama3.1", "ollama:qwen2.5-coder:7b"], anyModel: "ollama:<model>" },
  ],
};

test("what can be used right now comes first, and each model says why it can or cannot", () => {
  expect(modelChoices(listing)).toEqual([
    { id: "gpt-5.4", note: "API · server key", ready: true },
    { id: "deepseek-chat", note: "API · your key", ready: true },
    { id: "llama3.1", note: "local · installed", ready: true },
    { id: "ollama:qwen2.5-coder:7b", note: "local · installed", ready: true },
    { id: "claude-opus-5-5", note: "API · needs a key", ready: false, help: "It needs a key first. From a terminal: cerberus key set anthropic" },
    { id: "claude-sonnet-5-5", note: "API · needs a key", ready: false, help: "It needs a key first. From a terminal: cerberus key set anthropic" },
  ]);
});

test("a local server that does not answer is said so, and its models are only suggestions", () => {
  const silent: ModelListing = { default: "x", providers: [{ id: "ollama", name: "Ollama", kind: "local", ready: false, reachable: false, models: ["llama3.1"] }] };

  expect(modelChoices(silent)).toEqual([
    { id: "llama3.1", note: "local · not answering", ready: false, help: "Ollama is not answering. Start it, then pull the model: ollama pull <model>" },
  ]);
});

test("any model of a provider can be typed; what is not clearly one is not offered", () => {
  for (const id of ["ollama:qwen2.5-coder:7b", "ollama:hf.co/user/Model-GGUF:Q4_K_M", "anthropic:claude-opus-4-8", "openai:gpt-4.1", "deepseek:deepseek-coder", "claude-opus-4-6", "gpt-4o", "o3-mini"]) {
    expect(looksLikeModelId(id)).toBe(true);
  }
  for (const text of ["mistral", "llama", "ollama:", "groq:llama3", "gpt-4 turbo", "ollama:a b", ""]) {
    expect(looksLikeModelId(text)).toBe(false);
  }
});

test("typing narrows the list, and offers the typed id itself when it is not listed", () => {
  const choices = modelChoices(listing);

  expect(draftChoices("deep", choices)).toEqual([{ key: "pick:deepseek-chat", label: "deepseek-chat", hint: "API · your key" }]);
  expect(draftChoices("ollama:mistral-small", choices)).toEqual([
    { key: "typed", label: "Use ollama:mistral-small", hint: "as typed" },
  ]);
  // A listed id typed in full is offered once, as the listed model.
  expect(draftChoices("ollama:qwen2.5-coder:7b", choices).map((c) => c.key)).toEqual(["pick:ollama:qwen2.5-coder:7b"]);
  expect(draftChoices("mistral", choices)).toEqual([]);
});
