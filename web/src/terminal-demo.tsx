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
      className="terminal-demo"
      aria-label="Interactive terminal preview"
    >
      <div className="terminal-meta">
        <span>powershell</span>
        <span>workspace / vex</span>
      </div>
      <div className="terminal-workspace">
        <aside className="project-list" aria-label="Open projects">
          <span className="project-label">projects</span>
          <span className="project project-active">vex</span>
          <span className="project">kero</span>
          <span className="project">dotfiles</span>
        </aside>
        <div className="terminal-screen">
          <div className="terminal-tabs">
            <span className="tab tab-active">terminal</span>
            <span className="tab">mainwindow.xaml</span>
          </div>
          <div className="terminal-output" aria-live="polite">
            {lines.map((line, index) => (
              <div className="terminal-line" key={`${line.command}-${index}`}>
                <div>
                  <span className="prompt">PS</span> C:\work\vex&gt;{" "}
                  {line.command}
                </div>
                <div className="terminal-result">{line.output}</div>
              </div>
            ))}
            <form className="terminal-form" onSubmit={submitCommand}>
              <label htmlFor="terminal-command">
                <span className="prompt">PS</span> C:\work\vex&gt;
              </label>
              <input
                id="terminal-command"
                value={command}
                onChange={(event) => setCommand(event.target.value)}
                aria-label="Run a terminal command"
                autoComplete="off"
                spellCheck="false"
              />
              <span className="cursor" aria-hidden="true" />
            </form>
          </div>
        </div>
      </div>
      <p className="terminal-hint">try `help` or `vex split`</p>
    </section>
  );
}
