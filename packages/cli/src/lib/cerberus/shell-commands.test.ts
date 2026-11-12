import { describe, expect, test } from "bun:test";
import { isCovered, parseCommandLine, ruleFor, rulesToRemember } from "./shell-commands";

describe("parseCommandLine", () => {
  test("splits at &&, ||, ; and | into simple commands", () => {
    expect(parseCommandLine("git status")).toEqual({ commands: [["git", "status"]], complex: false });
    expect(parseCommandLine("cd app && npm test || echo failed; ls | wc -l").commands).toEqual([
      ["cd", "app"], ["npm", "test"], ["echo", "failed"], ["ls"], ["wc", "-l"],
    ]);
    expect(parseCommandLine("git add .\ngit status").commands).toEqual([["git", "add", "."], ["git", "status"]]);
  });

  test("quotes keep their words together, and separators inside them are just text", () => {
    expect(parseCommandLine(`git commit -m "fix: a && b; c | d"`)).toEqual({
      commands: [["git", "commit", "-m", "fix: a && b; c | d"]], complex: false,
    });
    expect(parseCommandLine(`grep 'a|b' file\\ name.txt`).commands).toEqual([["grep", "a|b", "file name.txt"]]);
    expect(parseCommandLine(`echo "say \\"hi\\""`).commands).toEqual([["echo", 'say "hi"']]);
  });

  test("anything the shell would expand, redirect or group makes the line complex", () => {
    for (const line of [
      "echo $(whoami)", "echo `whoami`", 'echo "$(rm -rf x)"', 'echo "$HOME"', "ls > out.txt", "cat < in.txt", "cat <<EOF",
      "(cd x && ls)", "{ ls; }", "sleep 5 &", "ls $DIR", "git status # with a comment", "FOO=1 npm test", "PATH=/tmp:$PATH ls",
    ]) {
      expect(parseCommandLine(line).complex).toBe(true);
    }
  });

  test("a line that cannot be read to its end is complex", () => {
    for (const line of [`echo "unterminated`, "echo 'unterminated", "ls &&", "&& ls", "ls ;; ls", "ls |", "", "   ", "echo \\"]) {
      expect(parseCommandLine(line).complex).toBe(true);
    }
  });
});

describe("ruleFor", () => {
  test("is the command and its subcommand, or the command alone when that says it all", () => {
    expect(ruleFor(["git", "status", "-s"])).toBe("git status");
    expect(ruleFor(["npm", "test"])).toBe("npm test");
    expect(ruleFor(["npm", "run", "build", "--", "--watch"])).toBe("npm run build");
    expect(ruleFor(["docker", "compose", "up", "-d"])).toBe("docker compose up");
    expect(ruleFor(["dotnet", "test", "Cerberus.slnx"])).toBe("dotnet test");
    expect(ruleFor(["ls", "-la", "src"])).toBe("ls");
    expect(ruleFor(["cat", "README.md"])).toBe("cat");
    expect(ruleFor(["tsc", "--noEmit"])).toBe("tsc");          // an option is not a subcommand
    expect(ruleFor(["pytest"])).toBe("pytest");
    expect(ruleFor(["./gradlew", "build"])).toBe("./gradlew build");
  });

  test("a command that can run or destroy anything gets no rule, however it is spelled", () => {
    for (const words of [["rm", "-rf", "build"], ["sudo", "ls"], ["bash", "-c", "x"], ["python3", "s.py"], ["curl", "http://x"],
      ["/bin/rm", "x"], ["find", ".", "-delete"], ["xargs", "rm"], ["env", "X=1", "ls"], ["sed", "-i", "s/a/b/", "f"]]) {
      expect(ruleFor(words)).toBeNull();
    }
  });
});

describe("isCovered", () => {
  const rules = ["git status", "npm test", "ls", "cd"];

  test("a command covered by a rule runs; what the rule does not name still asks", () => {
    expect(isCovered("git status", rules)).toBe(true);
    expect(isCovered("git status --short", rules)).toBe(true);
    expect(isCovered("ls -la src", rules)).toBe(true);
    expect(isCovered("git push", rules)).toBe(false);
    expect(isCovered("git statusx", rules)).toBe(false);          // a word, not a string prefix
    expect(isCovered("git -C /elsewhere status", rules)).toBe(false);
    expect(isCovered("npm install", rules)).toBe(false);
    expect(isCovered("git status", [])).toBe(false);
  });

  test("every command of the line must be covered", () => {
    expect(isCovered("cd app && npm test", rules)).toBe(true);
    expect(isCovered("git status | ls", rules)).toBe(true);
    expect(isCovered("git status && rm -rf .", rules)).toBe(false);
    expect(isCovered("git status; npm install", rules)).toBe(false);
    expect(isCovered("ls\ngit push --force", rules)).toBe(false);
  });

  test("a line that cannot be judged word by word is never covered", () => {
    for (const line of ["git status > /etc/passwd", "ls $(rm -rf x)", "ls `rm -rf x`", 'git status "$(reboot)"', "git status &", "X=1 git status", "git status &&"]) {
      expect(isCovered(line, rules)).toBe(false);
    }
  });

  test("a rule does not reach files that hold secrets", () => {
    const reading = ["cat", "head", "grep", "ls"];
    expect(isCovered("cat README.md", reading)).toBe(true);
    for (const line of ["cat .env", "head -5 config/.env.production", "cat ~/.ssh/id_ed25519", "grep token ~/.aws/credentials", "cat keys/server.pem", "ls ~/.ssh"]) {
      expect(isCovered(line, reading)).toBe(false);
    }
    expect(isCovered("cat .env.example", reading)).toBe(true);   // a template holds no secret
  });

  test("a rule for a never-remembered command — however it got there — covers nothing", () => {
    expect(isCovered("rm -rf build", ["rm", "rm -rf"])).toBe(false);
    expect(isCovered("/bin/rm x", ["/bin/rm x"])).toBe(false);
    expect(isCovered("ls && sudo ls", ["ls", "sudo ls"])).toBe(false);
  });
});

describe("rulesToRemember", () => {
  test("one rule per command of the line, without repeats", () => {
    expect(rulesToRemember("git status")).toEqual(["git status"]);
    expect(rulesToRemember("cd app && npm test && npm test -- --watch")).toEqual(["cd", "npm test"]);
    expect(rulesToRemember("git diff | head -50")).toEqual(["git diff", "head"]);
  });

  test("nothing is offered when the line is complex or holds a command that is always asked about", () => {
    expect(rulesToRemember("npm test > log.txt")).toBeNull();
    expect(rulesToRemember("git status && rm -rf node_modules")).toBeNull();
    expect(rulesToRemember("python3 manage.py migrate")).toBeNull();
    expect(rulesToRemember("echo $(date)")).toBeNull();
    expect(rulesToRemember("")).toBeNull();
  });

  test("what would be remembered for a line is exactly what then covers it", () => {
    for (const line of ["git status -s", "cd app && npm run build", "ls -la | wc -l", "dotnet test Cerberus.slnx", "./gradlew build --info"]) {
      const rules = rulesToRemember(line)!;
      expect(isCovered(line, rules)).toBe(true);
    }
  });
});
