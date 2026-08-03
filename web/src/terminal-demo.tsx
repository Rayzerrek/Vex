import { type FormEvent, startTransition, useState } from "react";

type TerminalLine = {
  command: string;
  output: string;
};

const initialTerminalLines: TerminalLine[] = [
  { command: "vex start", output: "workspace ready. 3 projects mounted." },
  {
    command: "dotnet build",
    output: "Build succeeded. 0 Warning(s) 0 Error(s)"
  }
];

const commandOutput: Record<string, string> = {
  help: "vex start | vex split | vex projects | clear",
  "vex split": "split pane created. focus stays in the terminal.",
  "vex projects": "vex   kero   dotfiles",
  clear: ""
};

export function TerminalDemo() {
  const [lines, setLines] = useState(initialTerminalLines);
  const [command, setCommand] = useState("");

  const submitCommand = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const nextCommand = command.trim();

    if (!nextCommand) {
      return;
    }

    startTransition(() => {
      if (nextCommand === "clear") {
        setLines([]);
      } else {
        setLines((current) => [
          ...current,
          {
            command: nextCommand,
            output: commandOutput[nextCommand] ?? "command not found"
          }
        ]);
      }
      setCommand("");
    });
  };

  return (
    <section
      className="min-w-0 border border-line bg-surface"
      aria-label="Interactive terminal preview"
    >
      <div className="flex min-h-[38px] items-center justify-between border-b border-line px-4 font-mono text-[10px] lowercase tracking-[0.08em] text-muted">
        <span>powershell</span>
        <span>workspace / vex</span>
      </div>
      <div className="grid min-h-[390px] grid-cols-[126px_minmax(0,1fr)] max-narrow:grid-cols-1">
        <aside
          className="flex flex-col gap-1 border-r border-line px-3 py-4.5 font-mono text-[11px] max-narrow:grid max-narrow:min-h-12 max-narrow:grid-cols-[auto_repeat(3,1fr)] max-narrow:items-center max-narrow:gap-2 max-narrow:border-b max-narrow:border-r-0 max-narrow:px-3 max-narrow:py-2.5"
          aria-label="Open projects"
        >
          <span className="mb-3 font-mono text-[10px] lowercase tracking-[0.08em] text-faint max-narrow:mb-0">
            projects
          </span>
          <span className="bg-surface-strong px-2 py-[7px] text-text max-narrow:p-[5px] max-narrow:text-center">
            vex
          </span>
          <span className="px-2 py-[7px] text-muted max-narrow:p-[5px] max-narrow:text-center">
            kero
          </span>
          <span className="px-2 py-[7px] text-muted max-narrow:p-[5px] max-narrow:text-center">
            dotfiles
          </span>
        </aside>
        <div className="flex min-w-0 flex-col">
          <div className="flex min-h-[38px] items-center gap-5.5 border-b border-line px-4 font-mono text-[10px] text-faint max-narrow:gap-3 max-narrow:overflow-hidden max-narrow:whitespace-nowrap">
            <span className="inline-flex h-[38px] items-center border-b border-accent text-text">
              terminal
            </span>
            <span className="inline-flex h-[38px] items-center">
              mainwindow.xaml
            </span>
          </div>
          <div
            className="flex min-h-0 flex-1 flex-col justify-end overflow-auto px-4.5 pb-4.5 pt-5.5 font-mono text-[11px] leading-[1.8]"
            aria-live="polite"
          >
            {lines.map((line, index) => (
              <div
                className="mt-2.5 first:mt-0"
                key={`${line.command}-${index}`}
              >
                <div>
                  <span className="text-accent">PS</span> C:\work\vex&gt;{" "}
                  {line.command}
                </div>
                <div className="pl-4.5 text-muted">{line.output}</div>
              </div>
            ))}
            <form
              className="mt-4 flex items-center gap-1.75 border-t border-line pt-3.5"
              onSubmit={submitCommand}
            >
              <label htmlFor="terminal-command" className="shrink-0 text-muted">
                <span className="text-accent">PS</span> C:\work\vex&gt;
              </label>
              <input
                id="terminal-command"
                value={command}
                onChange={(event) => setCommand(event.target.value)}
                aria-label="Run a terminal command"
                autoComplete="off"
                spellCheck="false"
                className="min-w-0 flex-1 border-0 bg-transparent p-0 font-mono text-[11px] text-text outline-0"
              />
              <span
                className="size-1.5 h-3.5 animate-blink bg-accent"
                aria-hidden="true"
              />
            </form>
          </div>
        </div>
      </div>
      <p className="border-t border-line px-4 py-3 font-mono text-[10px] lowercase tracking-[0.08em] text-faint">
        try `help` or `vex split`
      </p>
    </section>
  );
}
