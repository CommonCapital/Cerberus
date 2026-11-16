import type { Command } from "./types";
import { COMMANDS } from "./commands";

// Custom commands (prompt files of the project or the user) are listed after the built-in ones.
let customCommands: Command[] = [];

/** Replaces the custom commands shown in the menu. Built-in names always win. */
export function setCustomCommands(commands: Command[]) {
  const builtIn = new Set(COMMANDS.map((command) => command.name));
  customCommands = commands.filter((command) => !builtIn.has(command.name));
}

export const builtInCommandNames = () => new Set(COMMANDS.map((command) => command.name));

export function getFilteredCommands(query: string): Command[] {
  const all = [...COMMANDS, ...customCommands];
  if (query.length === 0) return all;
  return all.filter((cmd) => cmd.name.toLowerCase().startsWith(query.toLowerCase()));
};
