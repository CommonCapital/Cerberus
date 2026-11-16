type ChatFetch = (input: Parameters<typeof fetch>[0], init?: RequestInit) => Promise<Response>;

/**
 * A .NET server queues turns; the TypeScript chat backend only has /chat.
 * Discover the missing queue route from its HTTP response, then retain the
 * direct stream for this transport/address. An application-level JSON 404
 * (for example, an unknown session) is an error, not a missing capability.
 * Never replay an accepted turn, an auth failure, or an overloaded server.
 */
export function compatibleChatFetch(queued: ChatFetch, direct: ChatFetch): ChatFetch {
  const directAddresses = new Set<string>();
  return async (input, init) => {
    const address = input instanceof Request ? input.url : String(input);
    const signal = init?.signal ?? (input instanceof Request ? input.signal : undefined);
    signal?.throwIfAborted();
    if (directAddresses.has(address)) return direct(input, init);

    const response = await queued(input, init);
    const missing = response.status === 405 ||
      (response.status === 404 && !response.headers.get("content-type")?.toLowerCase().includes("json"));
    if (!missing) return response;

    // Cancelling while capability discovery was pending must not start work.
    signal?.throwIfAborted();
    const streamed = await direct(input, init);
    if (streamed.ok) directAddresses.add(address);
    return streamed;
  };
}
