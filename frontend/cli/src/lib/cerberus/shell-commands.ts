// Command rules: "always allow `git status`" instead of "always allow every
// command". A command line is split into the simple commands it runs; it may
// run without asking only if every one of them starts with a rule the operator
// approved. Whatever this cannot take apart with certainty — substitution,
// redirection, subshells, variable assignments — is never covered by a rule
// and is asked about, as before. When in doubt, ask.

import { inSecretDirectory, looksSecret } from "./sensitive-files";

/**
 * Commands that run other commands or code, reach the network, or destroy
 * things. "Always allow" for one of these would be "always allow anything",
 * so it is never offered and never matches a rule. (The backend keeps the
 * same list — Cerberus.Api/PermissionEndpoints.cs — and refuses such rules.)
 */
export const NEVER_REMEMBERED = new Set([
  "rm", "rmdir", "sudo", "su", "doas", "dd", "mkfs", "shred", "truncate", "shutdown", "reboot", "halt",
  "kill", "killall", "pkill", "eval", "exec", "source", ".", "sh", "bash", "zsh", "fish", "dash", "ksh",
  "python", "python2", "python3", "node", "ruby", "perl", "php", "deno", "npx", "bunx", "osascript", "pwsh", "powershell",
  "curl", "wget", "ssh", "scp", "sftp", "nc", "ncat", "telnet", "ftp",
  "find", "xargs", "env", "nohup", "watch", "awk", "gawk", "sed", "tee",
  "chmod", "chown", "chgrp", "ln", "crontab", "launchctl", "systemctl", "service", "mount", "umount", "diskutil",
]);

/** Commands whose name alone says what they do: the rule is the name. */
const SINGLE = new Set([
  "ls", "pwd", "cat", "head", "tail", "wc", "echo", "which", "whoami", "date", "uname", "tree", "du", "df",
  "grep", "rg", "diff", "sort", "uniq", "cut", "stat", "file", "basename", "dirname", "true", "test", "cd", "mkdir", "touch",
]);

/** Commands with a sub-subcommand: the rule takes three words ("npm run build", "docker compose up"). */
const THREE = new Set(["npm run", "yarn run", "pnpm run", "bun run", "docker compose", "git stash", "git remote", "dotnet tool"]);

export type ParsedLine = {
  /** The simple commands the line runs, each as its words. */
  commands: string[][];
  /** True when the line uses something that cannot be judged word by word; such a line is always asked about. */
  complex: boolean;
};

/**
 * Splits a command line into simple commands at `&&`, `||`, `;`, `|` and line
 * breaks, honouring quotes. Anything beyond plain words and those separators
 * marks the line as complex.
 */
export function parseCommandLine(line: string): ParsedLine {
  const commands: string[][] = [];
  let words: string[] = [];
  let word = "";
  let inWord = false;
  let complex = false;

  const endWord = () => {
    if (inWord) words.push(word);
    word = "";
    inWord = false;
  };
  const endCommand = () => {
    endWord();
    if (words.length === 0) complex = true;   // "a && && b", a leading or trailing separator
    else commands.push(words);
    words = [];
  };

  for (let i = 0; i < line.length; i++) {
    const c = line[i]!;

    if (c === "'") {
      const close = line.indexOf("'", i + 1);
      if (close === -1) return { commands: [], complex: true };
      word += line.slice(i + 1, close);
      inWord = true;
      i = close;
    } else if (c === '"') {
      inWord = true;
      for (i++; ; i++) {
        if (i >= line.length) return { commands: [], complex: true };
        const d = line[i]!;
        if (d === '"') break;
        // Inside double quotes the shell still expands these.
        if (d === "$" || d === "`") complex = true;
        if (d === "\\" && i + 1 < line.length) word += line[++i]!;
        else word += d;
      }
    } else if (c === "\\") {
      if (i + 1 >= line.length) return { commands: [], complex: true };
      word += line[++i]!;
      inWord = true;
    } else if (c === " " || c === "\t") {
      endWord();
    } else if (c === "\n" || c === ";") {
      endCommand();
    } else if (c === "&" || c === "|") {
      if (line[i + 1] === c) {
        i++;
        endCommand();
      } else if (c === "|") {
        endCommand();
      } else {
        complex = true;   // a lone "&": the command is sent to the background
        endCommand();
      }
    } else if ("$`<>(){}".includes(c) || c === "#") {
      // Substitution, redirection, grouping, a comment: beyond judging word by word.
      complex = true;
      word += c;
      inWord = true;
    } else {
      word += c;
      inWord = true;
    }
  }
  // The line's last command; a line that ends in a separator is malformed or continues elsewhere.
  endWord();
  if (words.length > 0) commands.push(words);
  else if (commands.length === 0 || /[;&|]\s*$/.test(line)) complex = true;

  // "NAME=value command" changes the environment the command runs in.
  if (commands.some((command) => /^[A-Za-z_][A-Za-z0-9_]*=/.test(command[0]!))) complex = true;
  return { commands, complex };
}

const nameOf = (word: string) => (word.includes("/") ? word.slice(word.lastIndexOf("/") + 1) : word);

/**
 * The rule that would cover a simple command: its name, or name and
 * subcommand ("git status", "npm run build"). Null when the command is one
 * that is never given a blanket permission.
 */
export function ruleFor(words: string[]): string | null {
  const first = words[0];
  if (!first || NEVER_REMEMBERED.has(nameOf(first))) return null;
  if (SINGLE.has(first)) return first;

  const second = words[1];
  if (!second || second.startsWith("-")) return first;
  const two = `${first} ${second}`;
  const third = words[2];
  if (THREE.has(two)) return third && !third.startsWith("-") ? `${two} ${third}` : two;
  return two;
}

/** Whether a simple command starts with the rule, word for word ("git status" covers "git status -s", not "git statusx"). */
const startsWith = (words: string[], rule: string) => {
  const ruleWords = rule.split(" ");
  return ruleWords.length <= words.length && ruleWords.every((w, i) => words[i] === w);
};

/**
 * Whether the operator's rules cover this command line: it can be taken apart,
 * and every command in it starts with a rule and is not one of the never-remembered.
 */
export function isCovered(line: string, rules: readonly string[]): boolean {
  const { commands, complex } = parseCommandLine(line);
  if (complex || commands.length === 0) return false;
  // A rule for "cat" is not a rule for "cat .env": anything that names a secrets file is asked about.
  if (commands.some((words) => words.some((word) => looksSecret(word) || inSecretDirectory(word)))) return false;
  return commands.every(
    (words) => !NEVER_REMEMBERED.has(nameOf(words[0]!)) && rules.some((rule) => startsWith(words, rule)),
  );
}

/**
 * The rules that "always allow" would add for this command line — one per
 * command in it — or null when it cannot be offered: the line is complex, or
 * one of its commands is never given a blanket permission.
 */
export function rulesToRemember(line: string): string[] | null {
  const { commands, complex } = parseCommandLine(line);
  if (complex || commands.length === 0) return null;
  const rules: string[] = [];
  for (const words of commands) {
    const rule = ruleFor(words);
    if (rule === null) return null;
    if (!rules.includes(rule)) rules.push(rule);
  }
  return rules;
}
