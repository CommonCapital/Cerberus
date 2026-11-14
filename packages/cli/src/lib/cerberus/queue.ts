// Where a waiting run stands. The server decides whose turn it is fairly
// (PLAN.md §22 item 4): the next free place goes to whoever has the fewest
// runs going, so a long queue of one person's runs does not hold up another's.
// It reports a waiting job's place as { ahead, others }; this says it in words.

export type QueuePosition = {
  /** The user's own runs before this one: those running, and those queued earlier. */
  ahead: number;
  /** How many other people have work waiting or running. */
  others: number;
};

const runs = (n: number) => `${n} of your runs`;
const people = (n: number) => `${n} other ${n === 1 ? "person" : "people"}`;

/** One short line, e.g. "Waiting its turn: 2 of your runs are ahead of it · places are shared with 1 other person". */
export function describeQueue(position: QueuePosition | null | undefined): string | null {
  if (!position) return null;
  const mine =
    position.ahead === 0 ? "next of yours to start" : `${runs(position.ahead)} ${position.ahead === 1 ? "is" : "are"} ahead of it`;
  const shared = position.others === 0 ? "" : ` · places are shared with ${people(position.others)}`;
  return `Waiting its turn: ${mine}${shared}`;
}
