// The program's entry: with a command it runs without a screen (for
// pipelines), without one it is the interactive CLI. The screen's modules are
// only loaded when it is wanted — importing them takes over the terminal.

const args = process.argv.slice(2);

if (args.length > 0) {
  const { main } = await import("./headless/main");
  process.exit(await main(args));
} else {
  await import("./index");
}
