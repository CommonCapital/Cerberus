// One reply to one user prompt may delegate only this many times. Counting
// starts before a child session is created, so retries of failed starts do
// not buy an unbounded number of requests. Helpers run one at a time.
export const MAX_HELPERS_PER_REPLY = 4;

const interrupted = () => new Error("The helper was interrupted before it finished; it has no report.");

/** Stop waiting promptly, even if the operation has not honoured cancellation yet. */
function untilCancelled<T>(work: Promise<T>, signal: AbortSignal): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const abort = () => reject(interrupted());
    if (signal.aborted) abort();
    else signal.addEventListener("abort", abort, { once: true });
    // Keep listening to work after cancellation, so a late failure is handled.
    work.then(
      (value) => { signal.removeEventListener("abort", abort); resolve(value); },
      (error) => { signal.removeEventListener("abort", abort); reject(error); },
    );
  });
}

/**
 * The helper allowance and cancellation scope of a single user reply.
 * Keep this instance for every model/tool continuation; create the next one
 * only when the operator submits another prompt or opens another session.
 */
export class HelperReply<T> {
  private readonly controller = new AbortController();
  private readonly calls = new Map<string, Promise<T>>();
  private tail: Promise<void> = Promise.resolve();
  private admitted = 0;

  get signal(): AbortSignal { return this.controller.signal; }
  get callIds(): IterableIterator<string> { return this.calls.keys(); }

  /** Cancellation is permanent. Stopping a reply does not replenish its allowance. */
  cancel(): void { this.controller.abort(); }

  /** Replayed ids share their result and are never started or counted twice. */
  request(id: string, run: (signal: AbortSignal) => Promise<T>): { first: boolean; result: Promise<T> } {
    const previous = this.calls.get(id);
    if (previous) return { first: false, result: previous };

    let result: Promise<T>;
    if (this.signal.aborted) result = Promise.reject(interrupted());
    else if (this.admitted >= MAX_HELPERS_PER_REPLY) {
      result = Promise.reject(new Error(
        `This reply has reached its limit of ${MAX_HELPERS_PER_REPLY} helpers. Finish with the results already available; another user prompt gets a new allowance.`,
      ));
    } else {
      this.admitted++;
      const work = this.tail.then(() => {
        if (this.signal.aborted) throw interrupted();
        return run(this.signal);
      });
      result = untilCancelled(work, this.signal);
      this.tail = result.then(() => {}, () => {});
    }
    this.calls.set(id, result);
    return { first: true, result };
  }
}
