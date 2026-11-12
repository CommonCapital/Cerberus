import { expect, test } from "bun:test";
import type { ModelListing } from "../lib/model-choices";
import { KEY_EXIT, modelsReport, parseKey, runKey, type KeyDeps } from "./keys";

const parse = (line: string) => parseKey(line.split(" ").filter(Boolean));
const refusal = (line: string) => {
  const parsed = parse(line);
  if (parsed.kind !== "usage") throw new Error(`not refused: ${JSON.stringify(parsed)}`);
  return parsed.message;
};

test("key commands name a provider; models takes nothing", () => {
  expect(parse("key set anthropic")).toEqual({ kind: "set", provider: "anthropic", server: null });
  expect(parse("key set DeepSeek --server http://host:3000")).toEqual({ kind: "set", provider: "deepseek", server: "http://host:3000" });
  expect(parse("key remove openai")).toEqual({ kind: "remove", provider: "openai", server: null });
  expect(parse("key list")).toEqual({ kind: "list", server: null });
  expect(parse("models --server=http://h")).toEqual({ kind: "models", server: "http://h" });
  expect(parse("key").kind).toBe("help");
  expect(parse("key set --help").kind).toBe("help");
});

test("a key written on the command line is refused before it can be used", () => {
  const message = refusal("key set anthropic sk-ant-something");

  expect(message).toContain("never given on the command line");
  expect(message).toContain("shell's history");
});

test("what cannot be meant is refused", () => {
  expect(refusal("key set")).toContain("Say which provider");
  expect(refusal("key set ollama")).toContain("Ollama needs none");
  expect(refusal("key set groq")).toContain('"groq" is not a provider that takes a key');
  expect(refusal("key rotate anthropic")).toContain("not something key can do");
  expect(refusal("key list all")).toContain("takes no arguments");
  expect(refusal("models extra")).toContain("takes no arguments");
  expect(refusal("key set anthropic --server")).toContain("--server needs a value");
});

function harness(change: Partial<KeyDeps> = {}) {
  const seen = { out: [] as string[], log: [] as string[], stored: [] as { provider: string; key: string }[], removed: [] as string[], asked: [] as string[] };
  const deps: KeyDeps = {
    list: async () => [{ provider: "anthropic", hasKey: true }, { provider: "openai", hasKey: false }, { provider: "deepseek", hasKey: false }],
    set: async (provider, key) => void seen.stored.push({ provider, key }),
    remove: async (provider) => void seen.removed.push(provider),
    models: async () => ({ default: "claude-opus-5-5", providers: [] }),
    env: () => undefined,
    readSecret: async (prompt) => {
      seen.asked.push(prompt);
      return "sk-typed-secret-0123456789";
    },
    out: (line) => void seen.out.push(line),
    log: (line) => void seen.log.push(line),
    ...change,
  };
  return { deps, seen };
}

const everythingSaid = (seen: { out: string[]; log: string[] }) => [...seen.out, ...seen.log].join("\n");

test("a key in the environment is used without asking, and is never printed", async () => {
  const { deps, seen } = harness({ env: (name) => (name === "DEEPSEEK_API_KEY" ? "  sk-env-secret-0123456789\n" : undefined) });

  const code = await runKey({ kind: "set", provider: "deepseek", server: null }, deps);

  expect(code).toBe(KEY_EXIT.ok);
  expect(seen.stored).toEqual([{ provider: "deepseek", key: "sk-env-secret-0123456789" }]);
  expect(seen.asked).toEqual([]);
  expect(seen.out).toEqual(["Your DeepSeek key is stored on the server, encrypted (read from DEEPSEEK_API_KEY). Choose a model with /models."]);
  expect(everythingSaid(seen)).not.toContain("sk-env-secret");
});

test("without one it is asked for, unseen", async () => {
  const { deps, seen } = harness();

  const code = await runKey({ kind: "set", provider: "anthropic", server: null }, deps);

  expect(code).toBe(KEY_EXIT.ok);
  expect(seen.asked).toEqual(["Claude (Anthropic) API key (not shown as you type): "]);
  expect(seen.stored).toEqual([{ provider: "anthropic", key: "sk-typed-secret-0123456789" }]);
  expect(everythingSaid(seen)).not.toContain("sk-typed-secret");
});

test("nothing given, or something that is not a key, stores nothing", async () => {
  const nothing = harness({ readSecret: async () => null });
  expect(await runKey({ kind: "set", provider: "openai", server: null }, nothing.deps)).toBe(KEY_EXIT.failed);
  expect(nothing.seen.stored).toEqual([]);
  expect(nothing.seen.log[0]).toContain("No key was given. Set OPENAI_API_KEY");

  const pasted = harness({ readSecret: async () => "export OPENAI_API_KEY=oops i pasted the whole line" });
  expect(await runKey({ kind: "set", provider: "openai", server: null }, pasted.deps)).toBe(KEY_EXIT.failed);
  expect(pasted.seen.stored).toEqual([]);
  expect(everythingSaid(pasted.seen)).not.toContain("oops");
});

test("list says which providers have a key; remove deletes one; a refusing server is reported", async () => {
  const listed = harness();
  expect(await runKey({ kind: "list", server: null }, listed.deps)).toBe(KEY_EXIT.ok);
  expect(listed.seen.out).toEqual([
    "Claude (Anthropic)   key stored",
    "GPT (OpenAI)         no key  (cerberus key set openai)",
    "DeepSeek             no key  (cerberus key set deepseek)",
  ]);

  const removed = harness();
  expect(await runKey({ kind: "remove", provider: "anthropic", server: null }, removed.deps)).toBe(KEY_EXIT.ok);
  expect(removed.seen.removed).toEqual(["anthropic"]);

  const failing = harness({
    set: async () => {
      throw new Error("Cannot reach the server at http://localhost:3000. Is the backend running?");
    },
  });
  expect(await runKey({ kind: "set", provider: "anthropic", server: null }, failing.deps)).toBe(KEY_EXIT.failed);
  expect(failing.seen.log).toEqual(["Cannot reach the server at http://localhost:3000. Is the backend running?"]);
});

test("the models report says what is ready, what needs a key, and how to name any other model", () => {
  const listing: ModelListing = {
    default: "claude-opus-5-5",
    providers: [
      { id: "anthropic", name: "Claude (Anthropic API)", kind: "api", ready: false, keySource: null, models: ["claude-opus-5-5"], anyModel: "anthropic:<model>" },
      { id: "deepseek", name: "DeepSeek (DeepSeek API)", kind: "api", ready: true, keySource: "yours", models: ["deepseek-chat"], anyModel: "deepseek:<model>" },
      { id: "ollama", name: "Ollama (local models)", kind: "local", ready: true, reachable: true, models: ["ollama:qwen2.5-coder:7b"], anyModel: "ollama:<model>" },
    ],
  };

  expect(modelsReport(listing)).toEqual([
    "Claude (Anthropic API) — needs a key: cerberus key set anthropic",
    "  claude-opus-5-5  (default)",
    "  any other: anthropic:<model>",
    "DeepSeek (DeepSeek API) — ready (your key)",
    "  deepseek-chat",
    "  any other: deepseek:<model>",
    "Ollama (local models) — ready",
    "  ollama:qwen2.5-coder:7b",
    "  any other: ollama:<model>",
  ]);
  const silent = modelsReport({ default: "x", providers: [{ id: "ollama", name: "Ollama (local models)", kind: "local", ready: false, reachable: false, models: ["llama3.1"] }] });
  expect(silent[0]).toBe("Ollama (local models) — Ollama is not answering; these are suggestions");
});
