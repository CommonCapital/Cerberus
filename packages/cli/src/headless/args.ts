import type { SubmitJob } from "../lib/cerberus/api";

// The command line of `cerberus run`: one job, started and followed without
// a screen, for pipelines and scripts. Parsing is strict on purpose — an
// option that would silently do nothing is worse than a refusal, in a job
// whose exit code decides whether a build passes.

export const SEVERITIES = ["critical", "high", "medium", "low", "info"] as const;
export type Severity = (typeof SEVERITIES)[number];
export type FailOn = Severity | "none";

export const RUN_TYPES = ["scan", "review", "sast", "threat-model", "index"] as const;
export type RunType = (typeof RUN_TYPES)[number];

export type RunOptions = {
  job: SubmitJob;
  /** The build fails when a counted finding is at or above this severity. */
  failOn: FailOn;
  /** Also count findings in tests and documentation. */
  includeTests: boolean;
  sarif: string | null;
  report: string | null;
  json: boolean;
  quiet: boolean;
  /** Kinds of action approved in advance, by name. Anything else that needs approval stops the run. */
  approve: string[];
  timeoutMinutes: number;
  server: string | null;
};

export type Parsed = { kind: "help" } | { kind: "run"; options: RunOptions } | { kind: "usage"; message: string };

export const USAGE = `Usage: cerberus run <scan|review|sast|threat-model|index> [path] [options]

Runs one job on the Cerberus server without the interactive screen, waits for
it, and exits with a code a pipeline can act on. The path is the target as the
server sees it (default "."). Progress goes to stderr, the result to stdout.

Jobs
  scan           the rule-based scanner and the dependency check
  review         a planned review: scan, assessments, a written report
  sast           a model reads the code and reports vulnerabilities (--model)
  threat-model   a model maps components and threats (--model)
  index          build or refresh the semantic code index

Result
  --fail-on <severity>    critical, high, medium, low, info or none (default high)
  --include-tests         also count findings in tests and documentation
  --sarif <file>          write the findings as SARIF 2.1.0 (scan, review, sast)
  --report <file>         write the written report as Markdown (review, sast, threat-model)
  --json                  print the result as one JSON object
  --quiet                 no progress on stderr

Job options
  --model <id>            the model for sast and threat-model; for a review, the triage model
  --agent-model <id>      review: the model that runs the review's agents
  --agents <a,b>          review: sast, threat-model, evidence
  --catalogs <a,b>        review: compliance catalogs (default: all)
  --no-sca                scan, review: skip the dependency check
  --exclusions <mode>     scan, review: honour (default) or ignore the target's own ignore marks
  --max-steps <n>         sast, threat-model: the agent's step limit
  --instructions <text>   sast, threat-model: what to concentrate on
  --engagement <id>       run under this engagement

Running unattended
  --approve <kind,kind>   approve these kinds of action in advance, by name
                          (for example send-code-to-cloud-model). Anything that
                          needs an approval not named here stops the run.
  --timeout <minutes>     give up and cancel the job after this long (default 60)
  --server <url>          the server's address (default: API_URL)

Sign-in: the token in CERBERUS_TOKEN, or the one saved by /login.

Exit codes
  0   finished; no counted finding at or above --fail-on
  1   finished; findings at or above --fail-on
  2   the job did not finish (failed, cancelled, timed out, server not reachable, not signed in)
  3   the job needs a person (an approval not given in advance, or a question)
  64  the command line is wrong`;

const VALUE_FLAGS = new Set([
  "--fail-on", "--sarif", "--report", "--model", "--agent-model", "--agents", "--catalogs",
  "--exclusions", "--max-steps", "--instructions", "--engagement", "--approve", "--timeout", "--server",
]);
const SWITCHES = new Set(["--include-tests", "--json", "--quiet", "--no-sca"]);

/** Which job options make sense for which job. Result and unattended options apply to all. */
const JOB_FLAGS: Record<string, RunType[]> = {
  "--model": ["review", "sast", "threat-model"],
  "--agent-model": ["review"],
  "--agents": ["review"],
  "--catalogs": ["review"],
  "--no-sca": ["scan", "review"],
  "--exclusions": ["scan", "review"],
  "--max-steps": ["sast", "threat-model"],
  "--instructions": ["sast", "threat-model"],
  "--sarif": ["scan", "review", "sast"],
  "--report": ["review", "sast", "threat-model"],
  "--fail-on": ["scan", "review", "sast"],
  "--include-tests": ["scan", "review", "sast"],
};

const REVIEW_AGENTS = ["sast", "threat-model", "evidence"] as const;

const usage = (message: string): Parsed => ({ kind: "usage", message });
const list = (value: string) => value.split(",").map((part) => part.trim()).filter(Boolean);

/** Parses what follows the program name: `run scan . --fail-on high`. */
export function parseArgs(argv: string[]): Parsed {
  const [command, ...rest] = argv;
  if (command === "help" || command === "--help" || command === "-h") return { kind: "help" };
  if (command !== "run") return usage(`Unknown command "${command ?? ""}". The commands are "run", "schedule", "key" and "models"; without arguments the interactive CLI starts.`);

  const positional: string[] = [];
  const values = new Map<string, string>();
  const switches = new Set<string>();

  for (let i = 0; i < rest.length; i++) {
    const arg = rest[i]!;
    if (arg === "--help" || arg === "-h") return { kind: "help" };
    if (!arg.startsWith("-")) {
      positional.push(arg);
      continue;
    }
    const equals = arg.indexOf("=");
    const name = equals === -1 ? arg : arg.slice(0, equals);
    if (SWITCHES.has(name)) {
      if (equals !== -1) return usage(`${name} takes no value.`);
      switches.add(name);
    } else if (VALUE_FLAGS.has(name)) {
      const value = equals === -1 ? rest[++i] : arg.slice(equals + 1);
      if (value === undefined || value === "" || (equals === -1 && value.startsWith("--"))) return usage(`${name} needs a value.`);
      if (values.has(name)) return usage(`${name} is given twice.`);
      values.set(name, value);
    } else {
      return usage(`Unknown option ${name}.`);
    }
  }

  const type = positional[0] as RunType | undefined;
  if (!type) return usage(`Say what to run: ${RUN_TYPES.join(", ")}.`);
  if (!RUN_TYPES.includes(type)) return usage(`"${type}" is not something run can start. Choose one of: ${RUN_TYPES.join(", ")}.`);
  if (positional.length > 2) return usage(`One path at a time: got "${positional.slice(1).join('", "')}".`);
  const path = positional[1] ?? ".";

  for (const name of [...values.keys(), ...switches]) {
    const allowed = JOB_FLAGS[name];
    if (allowed && !allowed.includes(type)) return usage(`${name} does not apply to ${type} (it is for ${allowed.join(", ")}).`);
  }

  const failOn = (values.get("--fail-on") ?? "high").toLowerCase();
  if (failOn !== "none" && !SEVERITIES.includes(failOn as Severity)) {
    return usage(`--fail-on must be one of ${SEVERITIES.join(", ")} or none.`);
  }

  const exclusions = (values.get("--exclusions") ?? "honour").toLowerCase() as "honour" | "ignore";
  if (exclusions !== "honour" && exclusions !== "ignore") {
    // "ask" exists on the server, but there is nobody here to ask.
    return usage("--exclusions must be honour or ignore.");
  }

  let maxSteps: number | undefined;
  if (values.has("--max-steps")) {
    maxSteps = Number(values.get("--max-steps"));
    if (!Number.isInteger(maxSteps) || maxSteps < 1) return usage("--max-steps must be a whole number of at least 1.");
  }

  let timeoutMinutes = 60;
  if (values.has("--timeout")) {
    timeoutMinutes = Number(values.get("--timeout"));
    if (!Number.isFinite(timeoutMinutes) || timeoutMinutes <= 0) return usage("--timeout must be a number of minutes above 0.");
  }

  const model = values.get("--model");
  const engagementId = values.get("--engagement");
  const sca = switches.has("--no-sca") ? false : undefined;
  const instructions = values.get("--instructions");

  let job: SubmitJob;
  switch (type) {
    case "scan":
      job = { type, path, sca, exclusions, engagementId };
      break;
    case "index":
      job = { type, path, engagementId };
      break;
    case "sast":
    case "threat-model":
      if (!model) return usage(`${type} is run by a model: name it with --model.`);
      job = { type, path, model, maxSteps, instructions, engagementId };
      break;
    case "review": {
      const agents = values.has("--agents") ? list(values.get("--agents")!) : undefined;
      const unknown = agents?.find((agent) => !REVIEW_AGENTS.includes(agent as (typeof REVIEW_AGENTS)[number]));
      if (unknown) return usage(`"${unknown}" is not a review agent. Choose from: ${REVIEW_AGENTS.join(", ")}.`);
      const agentModel = values.get("--agent-model");
      if (agents && !agentModel) return usage("--agents needs --agent-model: the model that runs them.");
      job = {
        type,
        path,
        catalogs: values.has("--catalogs") ? list(values.get("--catalogs")!) : undefined,
        model,
        agentModel,
        agents: agents as (typeof REVIEW_AGENTS)[number][] | undefined,
        sca,
        exclusions,
        engagementId,
      };
      break;
    }
  }

  return {
    kind: "run",
    options: {
      job,
      failOn: failOn as FailOn,
      includeTests: switches.has("--include-tests"),
      sarif: values.get("--sarif") ?? null,
      report: values.get("--report") ?? null,
      json: switches.has("--json"),
      quiet: switches.has("--quiet"),
      approve: values.has("--approve") ? list(values.get("--approve")!) : [],
      timeoutMinutes,
      server: values.get("--server") ?? null,
    },
  };
}
