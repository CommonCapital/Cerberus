import { expect, test } from "bun:test";
import { compatibleChatFetch } from "./chat-transport";

const address = "http://localhost:3000/chat";

for (const status of [404, 405]) {
  test(`a missing queue route (${status}) sends the original request to the direct stream once`, async () => {
    let queued = 0;
    const direct: { input: unknown; init?: RequestInit }[] = [];
    const signal = new AbortController().signal;
    const init = {
      method: "POST",
      headers: { authorization: "Bearer operator", "content-type": "application/json" },
      body: JSON.stringify({ id: "session", context: { instructionFiles: [{ content: "Keep this instruction" }] } }),
      signal,
    };
    const stream = () => new Response("data: [DONE]\n\n", { headers: { "content-type": "text/event-stream" } });
    const request = compatibleChatFetch(
      async () => { queued++; return new Response("Not Found", { status }); },
      async (input, options) => { direct.push({ input, init: options }); return stream(); },
    );
    const first = await request(address, init);
    expect(await first.text()).toBe("data: [DONE]\n\n");
    await request(address, init);
    expect(queued).toBe(1);
    expect(direct).toHaveLength(2);
    expect(direct[0]!.input).toBe(address);
    expect(direct[0]!.init).toBe(init);
    expect(direct[0]!.init?.signal).toBe(signal);
  });
}

for (const status of [200, 202, 400, 401, 403, 409, 429, 500, 503]) {
  test(`queue status ${status} is returned untouched without another submission`, async () => {
    const response = new Response("original response", { status });
    const request = compatibleChatFetch(async () => response, async () => { throw new Error("must not resubmit"); });
    expect(await request(address, { method: "POST", body: "{}" })).toBe(response);
  });
}

test("a queued session's JSON 404 is not mistaken for a missing queue", async () => {
  const response = Response.json({ error: "Session not found" }, { status: 404 });
  const request = compatibleChatFetch(async () => response, async () => { throw new Error("must not resubmit"); });
  expect(await request(address)).toBe(response);
});

test("a transport failure cannot safely be retried as another submission", async () => {
  const request = compatibleChatFetch(
    async () => { throw new Error("connection lost after submitting"); },
    async () => { throw new Error("must not resubmit"); },
  );
  await expect(request(address)).rejects.toThrow("connection lost after submitting");
});

test("cancelling during capability discovery prevents the direct submission", async () => {
  const controller = new AbortController();
  const request = compatibleChatFetch(
    async () => { controller.abort(); return new Response(null, { status: 404 }); },
    async () => { throw new Error("must not resubmit"); },
  );
  await expect(request(address, { signal: controller.signal })).rejects.toMatchObject({ name: "AbortError" });
});

test("capabilities are separate for different server addresses", async () => {
  const queued: string[] = [];
  const request = compatibleChatFetch(
    async (input) => { queued.push(String(input)); return new Response(null, { status: 404 }); },
    async () => new Response("stream"),
  );
  await request(address);
  await request(address);
  await request("http://other:3000/chat");
  expect(queued).toEqual([address, "http://other:3000/chat"]);
});

test("an unsuccessful direct fallback does not permanently disable the queue", async () => {
  let calls = 0;
  const request = compatibleChatFetch(
    async () => { calls++; return new Response(null, { status: 404 }); },
    async () => new Response(null, { status: 401 }),
  );
  expect((await request(address)).status).toBe(401);
  await request(address);
  expect(calls).toBe(2);
});
