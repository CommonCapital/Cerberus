// Folding in a reply: when the agent looks around — reads, searches, listings —
// the calls say little one by one, and a long run of them pushes what it wrote
// and what it changed off the screen. A run of them is shown as one line that
// counts them. Anything that changes something, runs something, failed, or is
// still going is always shown in full.

type ToolLike = { type: string; toolName?: string; state?: string; toolCallId?: string };

const QUIET: Record<string, "read" | "search" | "listing" | "skill"> = {
  readFile: "read",
  grep: "search",
  glob: "search",
  searchCode: "search",
  searchSkills: "search",
  listDirectory: "listing",
  loadSkill: "skill",
};

/** A run is folded from this many calls on; fewer are as short unfolded. */
export const FOLD_FROM = 3;

const isTool = (part: ToolLike) => part.type === "dynamic-tool" || part.type.startsWith("tool-");
const nameOf = (part: ToolLike) => (part.type === "dynamic-tool" ? (part.toolName ?? "") : part.type.slice("tool-".length));

/** A finished, successful call of a tool that only looks. */
const isQuiet = (part: ToolLike) => isTool(part) && part.state === "output-available" && nameOf(part) in QUIET;

export type Shown<P> = { kind: "part"; part: P; index: number } | { kind: "fold"; key: string; count: number; summary: string };

const WORDS = { read: ["read", "reads"], search: ["search", "searches"], listing: ["listing", "listings"], skill: ["skill", "skills"] } as const;

/** "4 reads, 2 searches, 1 listing" */
export function describeRun(names: string[]): string {
  return (["read", "search", "listing", "skill"] as const)
    .map((kind) => ({ kind, count: names.filter((name) => QUIET[name] === kind).length }))
    .filter((entry) => entry.count > 0)
    .map((entry) => `${entry.count} ${WORDS[entry.kind][entry.count === 1 ? 0 : 1]}`)
    .join(", ");
}

/** The parts of a reply as they are shown: runs of quiet calls folded into one line each, unless expanded. */
export function foldQuietRuns<P extends ToolLike>(parts: readonly P[], expanded: boolean): Shown<P>[] {
  const shown: Shown<P>[] = [];
  let run: { part: P; index: number }[] = [];

  const flush = () => {
    if (run.length >= FOLD_FROM && !expanded) {
      shown.push({
        kind: "fold",
        key: `fold-${run[0]!.part.toolCallId ?? run[0]!.index}`,
        count: run.length,
        summary: describeRun(run.map((entry) => nameOf(entry.part))),
      });
    } else {
      for (const entry of run) shown.push({ kind: "part", ...entry });
    }
    run = [];
  };

  parts.forEach((part, index) => {
    if (isQuiet(part)) {
      run.push({ part, index });
      return;
    }
    flush();
    shown.push({ kind: "part", part, index });
  });
  flush();
  return shown;
}
