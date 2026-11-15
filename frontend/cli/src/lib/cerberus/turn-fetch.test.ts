import { expect, test } from "bun:test";
import { turnFetch, turnHeaders, type TurnDeps, type TurnEvent, type TurnHandlers, type TurnPage, type TurnState } from "./turn-fetch";

/** A server and a push channel that do what the test tells them. */
function harness(options: { refuse?: Response; push?: boolean } = {}) {
  const stored: TurnEvent[] = [];
  let state: TurnState = "Running";
  let error: string | null = null;
  let handlers: TurnHandlers | null = null;
  let poll: (() => void) | null = null;
  const seen = { submitted: [] as string[], auth: [] as (string | undefined)[], read: [] as number[], cancelled: [] as string[], listening: 0, polling: 0 };

  const deps: TurnDeps = {
    submit: async (body, headers) => {
      seen.submitted.push(body);
      seen.auth.push(headers.authorization);
      return options.refuse ? { refused: options.refuse } : { turn: { turnId: "turn-1", state: "Queued", error: null, lastSequence: 0 } };
    },
    events: async (_turnId, after): Promise<TurnPage> => {
      seen.read.push(after);
      return { turnId: "turn-1", state, error, lastSequence: stored.at(-1)?.sequence ?? 0, events: stored.filter((e) => e.sequence > after), more: false };
    },
    cancel: async (turnId) => {
      seen.cancelled.push(turnId);
    },
    subscribe: (_turnId, given) => {
      seen.listening++;
      if (options.push !== false) handlers = given;
      return () => {
        seen.listening--;
        handlers = null;
      };
    },
    every: (_ms, callback) => {
      seen.polling++;
      poll = callback;
      return () => {
        seen.polling--;
        poll = null;
      };
    },
  };

  const chunk = (sequence: number, text: string): TurnEvent => ({ sequence, chunk: { type: "text-delta", id: "t0", delta: text } });
  return {
    deps,
    seen,
    chunk,
    /** The server writes a piece: stored, and pushed unless the test holds the push back. */
    write: (event: TurnEvent, pushed = true) => {
      stored.push(event);
      if (pushed) handlers?.event(event);
    },
    finish: (as: TurnState = "Completed", why: string | null = null) => {
      state = as;
      error = why;
      handlers?.changed();
    },
    poll: () => poll?.(),
  };
}

const post = (fetchLike: typeof fetch, signal?: AbortSignal) =>
  fetchLike("http://server/chat", { method: "POST", headers: { Authorization: "Bearer made-up" }, body: `{"id":"s1"}`, signal });

/** Everything the stream carries, as the lines the chat transport parses. */
async function read(response: Response): Promise<string[]> {
  const text = await response.text();
  return text.split("\n\n").filter(Boolean).map((line) => line.replace(/^data: /, ""));
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 5));

test("the request goes in as a turn and the reply comes back as the stream the transport expects", async () => {
  const h = harness();
  const response = await post(turnFetch(h.deps));
  await settle();

  h.write(h.chunk(1, "Hel"));
  h.write(h.chunk(2, "lo"));
  h.finish();

  expect(response.status).toBe(200);
  expect(response.headers.get("content-type")).toBe("text/event-stream");
  expect(response.headers.get("x-cerberus-turn")).toBe("turn-1");
  expect(await read(response)).toEqual([
    `{"type":"text-delta","id":"t0","delta":"Hel"}`,
    `{"type":"text-delta","id":"t0","delta":"lo"}`,
    "[DONE]",
  ]);
  // The body and the sign-in went to the server as they were given; afterwards nothing is left listening.
  expect(h.seen.submitted).toEqual([`{"id":"s1"}`]);
  expect(h.seen.auth).toEqual(["Bearer made-up"]);
  expect(h.seen).toMatchObject({ listening: 0, polling: 0 });
});

test("what was written before anyone listened is read from the stored turn", async () => {
  const h = harness();
  h.write(h.chunk(1, "early"));
  const response = await post(turnFetch(h.deps));
  await settle();
  h.write(h.chunk(2, " and live"));
  h.finish();

  expect((await read(response)).length).toBe(3);
  expect(h.seen.read[0]).toBe(0);
});

test("a piece that arrives out of line is not passed on until the gap is filled from the stored turn", async () => {
  const h = harness();
  const response = await post(turnFetch(h.deps));
  await settle();

  h.write(h.chunk(1, "one"));
  h.write(h.chunk(2, "two"), false);   // this push is lost
  h.write(h.chunk(3, "three"));
  await settle();
  h.write(h.chunk(3, "three"));        // and this one arrives twice
  h.finish();

  expect(await read(response)).toEqual([
    `{"type":"text-delta","id":"t0","delta":"one"}`,
    `{"type":"text-delta","id":"t0","delta":"two"}`,
    `{"type":"text-delta","id":"t0","delta":"three"}`,
    "[DONE]",
  ]);
});

test("without any push channel the poll alone delivers the whole reply", async () => {
  const h = harness({ push: false });
  const response = await post(turnFetch(h.deps));
  await settle();

  h.write(h.chunk(1, "slow"));
  h.write(h.chunk(2, " but sure"));
  h.poll();
  await settle();
  h.finish();
  h.poll();

  expect((await read(response)).slice(-2)).toEqual([`{"type":"text-delta","id":"t0","delta":" but sure"}`, "[DONE]"]);
});

test("a turn that broke off without saying so says it at the end; one that said so is not repeated", async () => {
  const silent = harness();
  const first = await post(turnFetch(silent.deps));
  await settle();
  silent.write(silent.chunk(1, "half"));
  silent.finish("Failed", "It took longer than 15 minutes.");
  expect((await read(first)).slice(-2)).toEqual([`{"type":"error","errorText":"It took longer than 15 minutes."}`, "[DONE]"]);

  const spoken = harness();
  const second = await post(turnFetch(spoken.deps));
  await settle();
  spoken.write({ sequence: 1, chunk: { type: "error", errorText: "The model refused the key." } });
  spoken.finish("Failed", "The reply could not be completed.");
  expect(await read(second)).toEqual([`{"type":"error","errorText":"The model refused the key."}`, "[DONE]"]);
});

test("stopping the reply tells the server to stop writing it", async () => {
  const h = harness();
  const stop = new AbortController();
  const response = await post(turnFetch(h.deps), stop.signal);
  await settle();
  h.write(h.chunk(1, "going on and"));

  stop.abort();
  h.write(h.chunk(2, " on"));

  expect(await read(response)).toEqual([`{"type":"text-delta","id":"t0","delta":"going on and"}`, "[DONE]"]);
  expect(h.seen.cancelled).toEqual(["turn-1"]);
  expect(h.seen).toMatchObject({ listening: 0, polling: 0 });
});

test("a request the server does not take comes back as the server's own answer", async () => {
  const h = harness({ refuse: new Response(`{"error":"Session not found"}`, { status: 404 }) });

  const response = await post(turnFetch(h.deps));

  expect(response.status).toBe(404);
  expect(await response.text()).toBe(`{"error":"Session not found"}`);
  expect(h.seen.listening).toBe(0);
});

test("the sign-in is sent once, whatever case the transport wrote it in", () => {
  const headers = turnHeaders({ authorization: "Bearer made-up", "x-extra": "kept" }, { Authorization: "Bearer made-up" });

  expect(headers.get("authorization")).toBe("Bearer made-up");
  expect(headers.get("content-type")).toBe("application/json");
  expect(headers.get("x-extra")).toBe("kept");
  // Ours wins over a stale one the transport may still carry.
  expect(turnHeaders({ authorization: "Bearer old" }, { Authorization: "Bearer new" }).get("authorization")).toBe("Bearer new");
  expect(turnHeaders({}, {}).get("authorization")).toBeNull();
});
