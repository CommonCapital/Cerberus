import { afterEach, expect, test } from "bun:test";
import { useEffect, type ReactNode } from "react";
import { testRender } from "@opentui/react/test-utils";
import { createMemoryRouter, RouterProvider } from "react-router";
import { RootLayout } from "../../layouts/root-layout";
import { useDialog } from "../../providers/dialog";
import type { ModelListing } from "../../lib/model-choices";
import { ModelsDialogContent } from "./models-dialog";
import { KeysDialogContent, type KeysIo } from "./keys-dialog";

function Opener({ title, children }: { title: string; children: ReactNode }) {
  const { open } = useDialog();
  useEffect(() => {
    open({ title, children });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);
  return <text>HOME</text>;
}

async function screen(title: string, dialog: ReactNode) {
  const router = createMemoryRouter([{ path: "/", element: <RootLayout />, children: [{ index: true, element: <Opener title={title}>{dialog}</Opener> }] }]);
  const rendered = await testRender(<RouterProvider router={router} />, { width: 150, height: 34 });
  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
    await rendered.renderOnce();
  };
  await settle();
  await settle();
  return { ...rendered, settle };
}

let current: Awaited<ReturnType<typeof screen>> | null = null;
afterEach(() => {
  current?.renderer.destroy();
  current = null;
});

const listing: ModelListing = {
  default: "claude-opus-5-5",
  providers: [
    { id: "anthropic", name: "Claude (Anthropic API)", kind: "api", ready: false, keySource: null, models: ["claude-opus-5-5"], anyModel: "anthropic:<model>" },
    { id: "deepseek", name: "DeepSeek (DeepSeek API)", kind: "api", ready: true, keySource: "yours", models: ["deepseek-chat"], anyModel: "deepseek:<model>" },
    { id: "ollama", name: "Ollama (local models)", kind: "local", ready: true, reachable: true, models: ["llama3.1", "ollama:qwen2.5-coder:7b"], anyModel: "ollama:<model>" },
  ],
};

test("the picker lists API and local models, what is ready first, and says how to name any other", async () => {
  const chosen: string[] = [];
  const ui = (current = await screen("Select Model", <ModelsDialogContent onSelectModel={(id) => chosen.push(id)} load={async () => listing} />));

  const text = ui.captureCharFrame();
  if (process.env.SHOW_FRAMES) console.log(text);
  expect(text).toContain("deepseek-chat");
  expect(text).toContain("API · your key");
  expect(text).toContain("ollama:qwen2.5-coder:7b");
  expect(text).toContain("local · installed");
  expect(text).toContain("claude-opus-5-5");
  expect(text).toContain("API · needs a key");
  expect(text.indexOf("deepseek-chat")).toBeLessThan(text.indexOf("claude-opus-5-5"));
  expect(text).toContain("Or type any model: ollama:name, openai:name");

  ui.mockInput.pressEnter();   // the first one that is ready
  await ui.settle();
  expect(chosen).toEqual(["deepseek-chat"]);
  expect(ui.captureCharFrame()).toContain("Model: deepseek-chat");
});

test("a model that needs a key can be chosen, and the operator is told how to add the key", async () => {
  const chosen: string[] = [];
  const ui = (current = await screen("Select Model", <ModelsDialogContent onSelectModel={(id) => chosen.push(id)} load={async () => listing} />));

  for (const key of "opus") ui.mockInput.pressKey(key);
  await ui.settle();
  ui.mockInput.pressEnter();
  await ui.settle();

  expect(chosen).toEqual(["claude-opus-5-5"]);
  expect(ui.captureCharFrame()).toContain("cerberus key set anthropic");
});

test("any model of a provider can be typed and chosen", async () => {
  const chosen: string[] = [];
  const ui = (current = await screen("Select Model", <ModelsDialogContent onSelectModel={(id) => chosen.push(id)} load={async () => listing} />));

  for (const key of "ollama:mistral-small") ui.mockInput.pressKey(key);
  await ui.settle();
  expect(ui.captureCharFrame()).toContain("Use ollama:mistral-small");
  ui.mockInput.pressEnter();
  await ui.settle();

  expect(chosen).toEqual(["ollama:mistral-small"]);
});

test("typing narrows the list to what matches", async () => {
  const chosen: string[] = [];
  const ui = (current = await screen("Select Model", <ModelsDialogContent onSelectModel={(id) => chosen.push(id)} load={async () => listing} />));

  for (const key of "qwen") ui.mockInput.pressKey(key);
  await ui.settle();
  const text = ui.captureCharFrame();
  expect(text).toContain("ollama:qwen2.5-coder:7b");
  expect(text).not.toContain("deepseek-chat");
  ui.mockInput.pressEnter();
  await ui.settle();

  expect(chosen).toEqual(["ollama:qwen2.5-coder:7b"]);
});

test("when the server cannot say, the known names are still offered", async () => {
  const ui = (current = await screen("Select Model", <ModelsDialogContent onSelectModel={() => {}} load={async () => { throw new Error("404"); }} />));

  const text = ui.captureCharFrame();
  expect(text).toContain("claude-opus-5-5");
  expect(text).toContain("llama3.1");
});

test("the keys dialog shows which providers have a key and never asks for one on screen", async () => {
  let keys = [{ provider: "anthropic", hasKey: true }, { provider: "openai", hasKey: false }, { provider: "deepseek", hasKey: false }];
  const removed: string[] = [];
  const io: KeysIo = {
    list: async () => keys,
    remove: async (provider) => {
      removed.push(provider);
      keys = keys.map((k) => (k.provider === provider ? { ...k, hasKey: false } : k));
    },
  };
  const ui = (current = await screen("API keys", <KeysDialogContent io={io} />));

  const text = ui.captureCharFrame();
  expect(text).toContain("Claude (Anthropic)");
  expect(text).toContain("key stored · enter removes it");
  expect(text).toContain("no key · cerberus key set deepseek");
  expect(text).toContain("Keys are set from a terminal");

  ui.mockInput.pressEnter();
  await ui.settle();
  await ui.settle();
  expect(removed).toEqual(["anthropic"]);
  expect(ui.captureCharFrame()).toContain("no key · cerberus key set anthropic");
});
