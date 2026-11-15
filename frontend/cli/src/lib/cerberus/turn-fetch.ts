// A conversation over the queue. The chat transport believes it is POSTing to
// the chat endpoint and reading the reply as the response; this stands in for
// `fetch` and does it the other way round: the request is handed to the server
// as a turn (accepted, queued under the signed-in user, answered by a worker),
// and the reply is put together here from what the server pushes over SignalR.
// Nothing depends on a push arriving: every piece has a sequence number, a gap
// is filled from the stored turn over HTTP, and a slow poll covers a push
// channel that is not there at all. The transport gets the same stream of
// chunks it would have got from the direct endpoint.

export type TurnState = "Queued" | "Running" | "Completed" | "Failed" | "Cancelled";

export type TurnEvent = { sequence: number; chunk: unknown };

export type TurnInfo = { turnId: string; state: TurnState; error: string | null; lastSequence: number };

export type TurnPage = TurnInfo & { events: TurnEvent[]; more: boolean };

export type TurnHandlers = {
  /** One piece of the reply, pushed as it was written. */
  event: (event: TurnEvent) => void;
  /** The turn started or ended, or the connection came back: time to read what may have been missed. */
  changed: () => void;
};

export type TurnDeps = {
  /** Hands the chat request to the server as a turn. Anything but an accepted turn comes back as the response it was. */
  submit: (body: string, headers: Record<string, string>) => Promise<{ turn: TurnInfo } | { refused: Response }>;
  /** The stored events after a sequence number, with the turn's state. */
  events: (turnId: string, after: number) => Promise<TurnPage>;
  cancel: (turnId: string) => Promise<unknown>;
  /** Listens for pushes about one turn. Returns how to stop listening. */
  subscribe: (turnId: string, handlers: TurnHandlers) => () => void;
  /** Calls back repeatedly; returns how to stop. */
  every: (ms: number, callback: () => void) => () => void;
};

/** How often the stored turn is read even when pushes arrive: the net under a push channel that silently stopped. */
export const SAFETY_POLL_MS = 2000;

const FINAL: TurnState[] = ["Completed", "Failed", "Cancelled"];

const encoder = new TextEncoder();
const frame = (data: string) => encoder.encode(`data: ${data}\n\n`);

const headersOf = (init?: RequestInit): Record<string, string> => {
  const out: Record<string, string> = {};
  new Headers(init?.headers).forEach((value, name) => {
    out[name] = value;
  });
  return out;
};

/**
 * The headers of a turn request. Set, not spread: the transport's own "authorization" and ours differ only in
 * case, and sent as two they arrive as one doubled value that no server accepts.
 */
export function turnHeaders(given: Record<string, string>, auth: Record<string, string>): Headers {
  const merged = new Headers(given);
  merged.set("content-type", "application/json");
  for (const [name, value] of Object.entries(auth)) merged.set(name, value);
  return merged;
}

/** A `fetch` for the chat transport that carries the conversation over the queue. */
export function turnFetch(deps: TurnDeps): typeof fetch {
  const viaQueue = async (_input: unknown, init?: RequestInit): Promise<Response> => {
    const signal = init?.signal ?? undefined;
    const submitted = await deps.submit(typeof init?.body === "string" ? init.body : "", headersOf(init));
    if ("refused" in submitted) return submitted.refused;
    const { turnId } = submitted.turn;

    let last = 0;
    let saidError = false;
    let closed = false;
    let reading: Promise<void> | null = null;
    let again = false;
    let stopListening = () => {};
    let stopPolling = () => {};
    let controller!: ReadableStreamDefaultController<Uint8Array>;

    const close = (finalError?: string | null) => {
      if (closed) return;
      closed = true;
      stopListening();
      stopPolling();
      signal?.removeEventListener("abort", onAbort);
      // A turn that broke off without saying so in its reply (timed out, the server restarted) says it here.
      if (finalError) controller.enqueue(frame(JSON.stringify({ type: "error", errorText: finalError })));
      controller.enqueue(frame("[DONE]"));
      controller.close();
    };

    /** Passes on the events that are next in line; says whether one arrived out of line. */
    const take = (events: TurnEvent[]): boolean => {
      let gap = false;
      for (const event of events) {
        if (closed || event.sequence <= last) continue;   // seen already: a push and a read overlap
        if (event.sequence !== last + 1) {
          gap = true;
          break;
        }
        last = event.sequence;
        if ((event.chunk as { type?: string } | null)?.type === "error") saidError = true;
        controller.enqueue(frame(JSON.stringify(event.chunk)));
      }
      return gap;
    };

    /** Reads what is stored after what we have. One read at a time; a request made meanwhile runs right after. */
    const catchUp = (): Promise<void> => {
      if (reading) {
        again = true;
        return reading;
      }
      reading = (async () => {
        try {
          do {
            again = false;
            let more = true;
            while (more && !closed) {
              const page = await deps.events(turnId, last);
              take(page.events);
              more = page.more;
              if (!more && FINAL.includes(page.state) && last >= page.lastSequence) {
                // Whole. An error the reply already carries is not said twice.
                close(page.state === "Failed" && !saidError ? page.error : null);
              }
            }
          } while (again && !closed);
        } catch {
          // The server did not answer this time; the poll asks again.
        } finally {
          reading = null;
        }
      })();
      return reading;
    };

    const onAbort = () => {
      // The operator stopped the reply: the server stops writing it, and this stream ends here.
      void deps.cancel(turnId).catch(() => {});
      close();
    };

    const body = new ReadableStream<Uint8Array>({
      start(streamController) {
        controller = streamController;
        if (signal?.aborted) {
          onAbort();
          return;
        }
        signal?.addEventListener("abort", onAbort, { once: true });
        stopListening = deps.subscribe(turnId, {
          event: (event) => {
            if (take([event])) void catchUp();
          },
          changed: () => void catchUp(),
        });
        stopPolling = deps.every(SAFETY_POLL_MS, () => void catchUp());
        // Whatever was written before we were listening.
        void catchUp();
      },
      cancel() {
        if (closed) return;
        closed = true;
        stopListening();
        stopPolling();
        signal?.removeEventListener("abort", onAbort);
      },
    });

    return new Response(body, {
      status: 200,
      headers: { "Content-Type": "text/event-stream", "x-vercel-ai-ui-message-stream": "v1", "x-cerberus-turn": turnId },
    });
  };
  return viaQueue as typeof fetch;
}
