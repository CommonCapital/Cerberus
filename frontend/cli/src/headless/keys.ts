import type { ModelListing } from "../lib/model-choices";

// `cerberus key …` and `cerberus models`: what an operator needs before any
// model can answer — a key for an API provider, and a look at what can be
// chosen. A key is read from the environment or typed without being shown;
// it is sent to the server, which keeps it encrypted, and it is never printed.

export const KEY_ENV: Record<string, string> = {
  anthropic: "ANTHROPIC_API_KEY",
  openai: "OPENAI_API_KEY",
  deepseek: "DEEPSEEK_API_KEY",
};

export const PROVIDER_NAMES: Record<string, string> = {
  anthropic: "Claude (Anthropic)",
  openai: "GPT (OpenAI)",
  deepseek: "DeepSeek",
};

export type KeyCommand =
  | { kind: "help" }
  | { kind: "usage"; message: string }
  | { kind: "models"; server: string | null }
  | { kind: "list"; server: string | null }
  | { kind: "set" | "remove"; provider: string; server: string | null };

export const KEY_USAGE = `Usage: cerberus key <set|list|remove> [provider]      cerberus models

  key set <anthropic|openai|deepseek>   store your API key for that provider on the server (encrypted).
                                        It is read from ${Object.values(KEY_ENV).join(" / ")}
                                        if that is set, from standard input if piped, or asked for
                                        without being shown. It is never printed.
  key list                              which providers have a key stored for you
  key remove <provider>                 delete your stored key
  models                                what can be chosen as a model, and what is ready to use

Local models (Ollama) need no key: pull one (ollama pull <model>) and choose it as ollama:<model>.
--server <url> names the server.

Exit codes: 0 done, 2 the server refused or could not be reached, 64 the command line is wrong`;

const usage = (message: string): KeyCommand => ({ kind: "usage", message });

/** Parses what follows the program name: `key set anthropic`, `models`. */
export function parseKey(argv: string[]): KeyCommand {
  const args = [...argv];
  let server: string | null = null;
  const at = args.findIndex((arg) => arg === "--server" || arg.startsWith("--server="));
  if (at >= 0) {
    const value = args[at] === "--server" ? args[at + 1] : args[at]!.slice("--server=".length);
    if (!value || value.startsWith("--")) return usage("--server needs a value.");
    server = value;
    args.splice(at, args[at] === "--server" ? 2 : 1);
  }
  if (args.includes("--help") || args.includes("-h")) return { kind: "help" };

  if (args[0] === "models") return args.length === 1 ? { kind: "models", server } : usage(`"models" takes no arguments.`);

  const [, action, provider, ...extra] = args;
  if (!action || action === "help") return { kind: "help" };
  if (action === "list") return provider === undefined ? { kind: "list", server } : usage(`"key list" takes no arguments.`);
  if (action !== "set" && action !== "remove") return usage(`"${action}" is not something key can do. Choose one of: set, list, remove.`);
  if (!provider) return usage(`Say which provider: cerberus key ${action} <${Object.keys(KEY_ENV).join("|")}>`);
  if (extra.length > 0) {
    // The mistake to catch before it lands in a shell history: a key written on the command line.
    return usage("Too many arguments. A key is never given on the command line (it would stay in the shell's history): put it in the environment or type it when asked.");
  }
  const known = provider.toLowerCase();
  if (!(known in KEY_ENV)) return usage(`"${provider}" is not a provider that takes a key. Choose one of: ${Object.keys(KEY_ENV).join(", ")}. Ollama needs none.`);
  return { kind: action, provider: known, server };
}

export type KeyDeps = {
  list: () => Promise<{ provider: string; hasKey: boolean }[]>;
  set: (provider: string, key: string) => Promise<unknown>;
  remove: (provider: string) => Promise<unknown>;
  models: () => Promise<ModelListing>;
  env: (name: string) => string | undefined;
  /** Asks for a secret without showing it (or reads piped input). Null when nothing was given. */
  readSecret: (prompt: string) => Promise<string | null>;
  out: (line: string) => void;
  log: (line: string) => void;
};

export const KEY_EXIT = { ok: 0, failed: 2, usage: 64 } as const;

/** What `cerberus models` prints: each provider, whether it is ready, its models, and how to name any other. */
export function modelsReport(listing: ModelListing): string[] {
  const lines: string[] = [];
  for (const provider of listing.providers) {
    const state =
      provider.id === "dev" ? "for trying things out"
      : provider.kind === "local"
        ? provider.reachable === false ? "Ollama is not answering; these are suggestions" : provider.ready ? "ready" : "no model is pulled yet (ollama pull <model>)"
        : provider.ready ? (provider.keySource === "server" ? "ready (the server's key)" : "ready (your key)") : `needs a key: cerberus key set ${provider.id}`;
    lines.push(`${provider.name} — ${state}`);
    for (const model of provider.models) lines.push(`  ${model}${model === listing.default ? "  (default)" : ""}`);
    if (provider.anyModel) lines.push(`  any other: ${provider.anyModel}`);
  }
  return lines;
}

export async function runKey(command: Exclude<KeyCommand, { kind: "help" | "usage" }>, deps: KeyDeps): Promise<number> {
  try {
    switch (command.kind) {
      case "models":
        for (const line of modelsReport(await deps.models())) deps.out(line);
        return KEY_EXIT.ok;
      case "list":
        for (const key of await deps.list()) {
          deps.out(`${(PROVIDER_NAMES[key.provider] ?? key.provider).padEnd(20)} ${key.hasKey ? "key stored" : `no key  (cerberus key set ${key.provider})`}`);
        }
        return KEY_EXIT.ok;
      case "remove":
        await deps.remove(command.provider);
        deps.out(`Your ${PROVIDER_NAMES[command.provider]} key was removed from the server.`);
        return KEY_EXIT.ok;
      case "set": {
        const name = PROVIDER_NAMES[command.provider]!;
        const variable = KEY_ENV[command.provider]!;
        const fromEnvironment = deps.env(variable)?.trim();
        const key = fromEnvironment || (await deps.readSecret(`${name} API key (not shown as you type): `))?.trim();
        if (!key) {
          deps.log(`No key was given. Set ${variable}, pipe the key in, or type it when asked.`);
          return KEY_EXIT.failed;
        }
        if (/\s/.test(key) || key.length < 8) {
          deps.log("That does not look like an API key (too short, or it has spaces in it). Nothing was stored.");
          return KEY_EXIT.failed;
        }
        await deps.set(command.provider, key);
        deps.out(`Your ${name} key is stored on the server, encrypted${fromEnvironment ? ` (read from ${variable})` : ""}. Choose a model with /models.`);
        return KEY_EXIT.ok;
      }
    }
  } catch (problem) {
    deps.log(problem instanceof Error ? problem.message : String(problem));
    return KEY_EXIT.failed;
  }
}

/** The real way to ask for a secret: piped input as it is; at a terminal, read without echo. */
export async function readSecretFromTerminal(prompt: string): Promise<string | null> {
  const stdin = process.stdin;
  if (!stdin.isTTY) {
    let piped = "";
    for await (const chunk of stdin) piped += chunk;
    return piped.trim() || null;
  }
  process.stderr.write(prompt);
  stdin.setRawMode(true);
  stdin.resume();
  try {
    let typed = "";
    for await (const chunk of stdin) {
      for (const char of chunk.toString("utf8")) {
        if (char === "\r" || char === "\n") return typed || null;
        if (char === "\u0003" || char === "\u001b") return null;                       // ctrl+c, escape: nothing given
        if (char === "\u007f" || char === "\b") typed = typed.slice(0, -1);
        else if (char >= " ") typed += char;
      }
    }
    return typed || null;
  } finally {
    stdin.setRawMode(false);
    stdin.pause();
    process.stderr.write("\n");
  }
}
