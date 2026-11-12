// `cerberus mcp`: which outside (MCP) servers this installation has configured,
// whether they answer, and — the part an operator should be able to see before
// approving a run — exactly which of their tools an agent's researcher is given
// and which are withheld, with the reason. Servers are configured on the server
// (CERBERUS_MCP_CONFIG), not from here.

export type McpListing = {
  configured: number;
  problems: string[];
  servers: {
    name: string;
    address: string;
    reachable: boolean;
    problem: string | null;
    serverName: string;
    protocol: string;
    tools: { name: string; tool: string; description: string }[];
    withheld: { tool: string; why: string }[];
  }[];
};

export type McpCommand =
  | { kind: "help" }
  | { kind: "usage"; message: string }
  | { kind: "list"; refresh: boolean; json: boolean; server: string | null };

export const MCP_USAGE = `Usage: cerberus mcp [--refresh] [--json]

Shows the outside servers (Model Context Protocol) configured on the Cerberus server:
whether each answers, which of its tools an agent's researcher may use, and which are
withheld and why. Agents get these tools only in a run started with --research, after
approval (use-mcp-servers), and only through the researcher that never sees the code.

  --refresh        ask the servers again instead of using what is remembered
  --json           print the listing as JSON
  --server <url>   the Cerberus server's address (default: API_URL)

Servers are named in a file on the Cerberus server (CERBERUS_MCP_CONFIG):
  { "servers": [ { "name": "docs", "url": "https://example.org/mcp",
                   "headers": { "Authorization": "env:DOCS_MCP_TOKEN" }, "tools": ["search"] } ] }
Without "tools", only the tools a server marks read-only are used.

Exit codes: 0 every configured server answers (or none is configured), 1 one does not or the
configuration has a problem, 2 the Cerberus server refused or could not be reached, 64 the command line is wrong`;

export const MCP_EXIT = { ok: 0, degraded: 1, failed: 2, usage: 64 } as const;

/** Parses what follows the program name: `mcp --refresh`. */
export function parseMcp(argv: string[]): McpCommand {
  const args = argv.slice(1);
  let server: string | null = null;
  let refresh = false;
  let json = false;
  for (let i = 0; i < args.length; i++) {
    const arg = args[i]!;
    if (arg === "--help" || arg === "-h" || arg === "help") return { kind: "help" };
    if (arg === "--refresh") refresh = true;
    else if (arg === "--json") json = true;
    else if (arg === "--server" || arg.startsWith("--server=")) {
      const value = arg === "--server" ? args[++i] : arg.slice("--server=".length);
      if (!value || value.startsWith("--")) return { kind: "usage", message: "--server needs a value." };
      server = value;
    } else {
      return { kind: "usage", message: `"${arg}" is not something mcp understands. Servers are configured on the Cerberus server, not from here.` };
    }
  }
  return { kind: "list", refresh, json, server };
}

/** The listing as lines for a terminal. */
export function mcpReport(listing: McpListing): string[] {
  const lines = listing.problems.map((problem) => `Configuration: ${problem}.`);
  if (listing.configured === 0) {
    lines.push("No outside servers are configured on this Cerberus server (CERBERUS_MCP_CONFIG). See: cerberus mcp --help");
    return lines;
  }
  for (const server of listing.servers) {
    if (!server.reachable) {
      lines.push(`${server.name} (${server.address}) — NOT AVAILABLE: ${server.problem ?? "it could not be reached"}`);
      continue;
    }
    lines.push(`${server.name} (${server.address}) — ok, ${server.tools.length} tool(s) for agents, ${server.withheld.length} withheld`);
    for (const tool of server.tools) lines.push(`  + ${tool.name}  (the server's ${tool.tool})`);
    for (const tool of server.withheld) lines.push(`  - ${tool.tool}: ${tool.why}`);
  }
  return lines;
}

export type McpDeps = {
  servers: (refresh: boolean) => Promise<McpListing>;
  out: (line: string) => void;
  log: (line: string) => void;
};

export async function runMcp(command: Extract<McpCommand, { kind: "list" }>, deps: McpDeps): Promise<number> {
  let listing: McpListing;
  try {
    listing = await deps.servers(command.refresh);
  } catch (problem) {
    deps.log(problem instanceof Error ? problem.message : String(problem));
    return MCP_EXIT.failed;
  }
  if (command.json) deps.out(JSON.stringify(listing, null, 2));
  else for (const line of mcpReport(listing)) deps.out(line);
  return listing.problems.length > 0 || listing.servers.some((server) => !server.reachable) ? MCP_EXIT.degraded : MCP_EXIT.ok;
}
