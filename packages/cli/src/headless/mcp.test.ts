import { describe, expect, test } from "bun:test";
import { MCP_EXIT, mcpReport, parseMcp, runMcp, type McpListing } from "./mcp";

const listing: McpListing = {
  configured: 2,
  problems: [],
  servers: [
    {
      name: "advisories", address: "https://advisories.example/mcp", reachable: true, problem: null, serverName: "Advisories", protocol: "2025-06-18",
      tools: [{ name: "mcp_advisories_search", tool: "search", description: "…" }],
      withheld: [{ tool: "create_ticket", why: "the server does not mark it read-only; name it under \"tools\" to allow it" }],
    },
    { name: "tickets", address: "http://tickets:8080/mcp", reachable: false, problem: "it could not be reached", serverName: "", protocol: "", tools: [], withheld: [] },
  ],
};

describe("cerberus mcp: the command line", () => {
  test("no arguments lists; flags are read in any order", () => {
    expect(parseMcp(["mcp"])).toEqual({ kind: "list", refresh: false, json: false, server: null });
    expect(parseMcp(["mcp", "--json", "--server=http://box:3000", "--refresh"])).toEqual({ kind: "list", refresh: true, json: true, server: "http://box:3000" });
    expect(parseMcp(["mcp", "--server", "http://box:3000"])).toEqual({ kind: "list", refresh: false, json: false, server: "http://box:3000" });
  });

  test("help, and what it does not understand", () => {
    expect(parseMcp(["mcp", "--help"])).toEqual({ kind: "help" });
    expect(parseMcp(["mcp", "--server"])).toEqual({ kind: "usage", message: "--server needs a value." });
    // Adding a server from the command line is not a thing: which outside services agents reach is the installation's decision.
    expect(parseMcp(["mcp", "add", "https://x.example/mcp"])).toEqual({
      kind: "usage", message: `"add" is not something mcp understands. Servers are configured on the Cerberus server, not from here.`,
    });
  });
});

describe("cerberus mcp: what it prints", () => {
  test("each server, what agents get of it, what is withheld and why", () => {
    expect(mcpReport(listing)).toEqual([
      "advisories (https://advisories.example/mcp) — ok, 1 tool(s) for agents, 1 withheld",
      "  + mcp_advisories_search  (the server's search)",
      `  - create_ticket: the server does not mark it read-only; name it under "tools" to allow it`,
      "tickets (http://tickets:8080/mcp) — NOT AVAILABLE: it could not be reached",
    ]);
  });

  test("nothing configured, and a configuration with a problem", () => {
    expect(mcpReport({ configured: 0, problems: [], servers: [] })).toEqual([
      "No outside servers are configured on this Cerberus server (CERBERUS_MCP_CONFIG). See: cerberus mcp --help",
    ]);
    expect(mcpReport({ configured: 0, problems: ["the MCP configuration file is not valid JSON"], servers: [] })[0]).toBe(
      "Configuration: the MCP configuration file is not valid JSON.",
    );
  });
});

describe("cerberus mcp: running it", () => {
  const run = async (answer: McpListing | Error, command = parseMcp(["mcp"])) => {
    const out: string[] = [];
    const log: string[] = [];
    const asked: boolean[] = [];
    if (command.kind !== "list") throw new Error("not a list command");
    const code = await runMcp(command, {
      servers: async (refresh) => {
        asked.push(refresh);
        if (answer instanceof Error) throw answer;
        return answer;
      },
      out: (line) => out.push(line),
      log: (line) => log.push(line),
    });
    return { code, out, log, asked };
  };

  test("the exit code says whether every server answers", async () => {
    expect((await run(listing)).code).toBe(MCP_EXIT.degraded);
    expect((await run({ ...listing, servers: [listing.servers[0]!] })).code).toBe(MCP_EXIT.ok);
    expect((await run({ configured: 0, problems: [], servers: [] })).code).toBe(MCP_EXIT.ok);
    expect((await run({ configured: 0, problems: ["broken"], servers: [] })).code).toBe(MCP_EXIT.degraded);
  });

  test("--refresh is passed on, --json prints the listing as it is", async () => {
    const result = await run(listing, parseMcp(["mcp", "--refresh", "--json"]));
    expect(result.asked).toEqual([true]);
    expect(JSON.parse(result.out.join("\n"))).toEqual(listing);
  });

  test("a server that cannot be asked is an error on stderr, not a listing", async () => {
    const result = await run(new Error("Not signed in."));
    expect(result).toMatchObject({ code: MCP_EXIT.failed, out: [], log: ["Not signed in."] });
  });
});
