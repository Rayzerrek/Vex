import { type CSSProperties, useEffect, useRef, useState } from "react";
import { themes, type TerminalTheme } from "./themes.ts";

const ansiKeys = ["red", "green", "yellow", "blue", "magenta", "cyan"] as const;

function themeStyle(theme: TerminalTheme) {
  return {
    "--tw-bg": theme.background,
    "--tw-fg": theme.foreground,
    "--tw-cursor": theme.cursor,
    "--tw-selection": theme.selectionBackground,
    "--tw-muted": theme.brightBlack,
    "--tw-line": theme.selectionBackground,
    "--tw-accent": theme.green,
    ...Object.fromEntries(ansiKeys.map((key) => [`--tw-${key}`, theme[key]]))
  } as CSSProperties;
}

export function ThemeWorkspace() {
  const [themeIndex, setThemeIndex] = useState(0);
  const [committedIndex, setCommittedIndex] = useState(0);
  const [open, setOpen] = useState(false);
  const selectedCard = useRef<HTMLButtonElement>(null);

  const showSwitcher = () => {
    setCommittedIndex(themeIndex);
    setOpen(true);
  };
  const keep = (index = themeIndex) => {
    setThemeIndex(index);
    setCommittedIndex(index);
    setOpen(false);
  };
  const revert = () => {
    setThemeIndex(committedIndex);
    setOpen(false);
  };

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.ctrlKey && event.shiftKey && event.key.toLowerCase() === "m") {
        event.preventDefault();
        open ? revert() : showSwitcher();
        return;
      }
      if (!open) return;
      const columns = window.innerWidth < 640 ? 2 : 4;
      const moves: Record<string, number> = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -columns, ArrowDown: columns };
      if (event.key in moves) {
        event.preventDefault();
        setThemeIndex((current) => (current + moves[event.key] + themes.length) % themes.length);
      } else if (event.key === "Enter") {
        event.preventDefault();
        keep();
      } else if (event.key === "Escape") {
        event.preventDefault();
        revert();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  useEffect(() => {
    if (open) selectedCard.current?.focus();
  }, [open]);

  const activeTheme = themes[themeIndex];
  return (
    <main className="theme-workspace min-h-dvh bg-[var(--tw-bg)] p-4 font-mono text-[var(--tw-fg)] sm:p-8" style={themeStyle(activeTheme)}>
      <div className="mx-auto flex min-h-[calc(100dvh-4rem)] max-w-[1280px] flex-col overflow-hidden border border-[var(--tw-line)] bg-[var(--tw-bg)] shadow-2xl">
        <header className="flex h-12 shrink-0 items-center justify-between border-b border-[var(--tw-line)] px-4 text-[11px]">
          <div className="flex items-center gap-3"><span className="font-bold text-[var(--tw-accent)]">vex</span><span className="text-[var(--tw-muted)]">workspace / vex</span></div>
          <button className="border border-[var(--tw-line)] px-3 py-1.5 lowercase hover:bg-[var(--tw-selection)]" onClick={showSwitcher}>themes <span className="ml-2 text-[var(--tw-muted)]">ctrl shift m</span></button>
        </header>
        <div className="grid min-h-0 flex-1 grid-cols-[150px_minmax(0,1fr)] max-sm:grid-cols-[92px_minmax(0,1fr)]">
          <aside className="border-r border-[var(--tw-line)] p-3 text-[11px]">
            <p className="mb-5 text-[9px] tracking-[.14em] text-[var(--tw-muted)]">projects</p>
            {['vex', 'kero', 'dotfiles'].map((item, index) => <div key={item} className={`mb-1 px-2 py-2 ${index === 0 ? 'bg-[var(--tw-selection)] text-[var(--tw-fg)]' : 'text-[var(--tw-muted)]'}`}>{index === 0 && <span className="mr-2 text-[var(--tw-accent)]">›</span>}{item}</div>)}
            <p className="mt-8 mb-3 text-[9px] tracking-[.14em] text-[var(--tw-muted)]">files</p>
            <div className="space-y-2 px-2 text-[var(--tw-muted)]"><p>⌄ src</p><p className="pl-3">app.tsx</p><p className="pl-3">shell.cs</p><p>readme.md</p></div>
          </aside>
          <section className="flex min-w-0 flex-col">
            <div className="flex h-11 shrink-0 items-stretch border-b border-[var(--tw-line)] text-[10px] text-[var(--tw-muted)]">
              <span className="flex items-center border-r border-[var(--tw-line)] border-b-2 border-b-[var(--tw-accent)] px-5 text-[var(--tw-fg)]">terminal</span><span className="flex items-center border-r border-[var(--tw-line)] px-5">mainwindow.xaml</span><span className="flex items-center px-5">settings.json</span>
            </div>
            <div className="flex-1 overflow-auto p-6 text-[12px] leading-7 max-sm:p-4">
              <p><span className="text-[var(--tw-green)]">PS</span> <span className="text-[var(--tw-blue)]">C:\work\vex</span>&gt; git status</p>
              <p className="text-[var(--tw-muted)]">On branch <span className="text-[var(--tw-magenta)]">main</span></p>
              <p className="text-[var(--tw-green)]">Your branch is up to date with 'origin/main'.</p>
              <br />
              <p><span className="text-[var(--tw-green)]">PS</span> <span className="text-[var(--tw-blue)]">C:\work\vex</span>&gt; dotnet build</p>
              <p><span className="text-[var(--tw-cyan)]">Determining projects to restore...</span></p>
              <p>All projects are up-to-date for restore.</p>
              <p><span className="text-[var(--tw-blue)]">Vex.App</span> → bin\Debug\net8.0-windows\Vex.App.dll</p>
              <p className="mt-3 text-[var(--tw-green)]">Build succeeded.</p>
              <p><span className="text-[var(--tw-yellow)]">0 Warning(s)</span> &nbsp; <span className="text-[var(--tw-green)]">0 Error(s)</span></p>
              <br />
              <p><span className="text-[var(--tw-green)]">PS</span> <span className="text-[var(--tw-blue)]">C:\work\vex</span>&gt; <span className="inline-block h-4 w-2 translate-y-1 bg-[var(--tw-cursor)] animate-blink" /></p>
            </div>
            <footer className="flex h-8 items-center justify-between border-t border-[var(--tw-line)] px-4 text-[9px] text-[var(--tw-muted)]"><span>powershell · utf-8</span><span className="text-[var(--tw-accent)]">{activeTheme.name.toLowerCase()}</span></footer>
          </section>
        </div>
      </div>

      <div className={`fixed inset-0 z-20 flex items-center justify-center bg-black/60 p-4 transition-opacity duration-200 ${open ? "opacity-100" : "pointer-events-none opacity-0"}`} aria-hidden={!open} onMouseDown={(event) => event.target === event.currentTarget && revert()}>
        <section role="dialog" aria-modal="true" aria-label="Choose terminal theme" className={`w-full max-w-3xl border border-[var(--tw-line)] bg-[var(--tw-bg)] p-5 shadow-2xl transition duration-200 ${open ? "scale-100 opacity-100" : "scale-95 opacity-0"}`}>
          <div className="mb-5 flex items-center justify-between"><div><h1 className="text-sm lowercase">choose theme</h1><p className="mt-1 text-[10px] text-[var(--tw-muted)]">chrome + terminal palette</p></div><span className="text-[10px] text-[var(--tw-accent)]">{activeTheme.name.toLowerCase()}</span></div>
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
            {themes.map((item, index) => <button ref={index === themeIndex ? selectedCard : undefined} tabIndex={open ? 0 : -1} key={item.name} onMouseEnter={() => setThemeIndex(index)} onFocus={() => setThemeIndex(index)} onClick={() => keep(index)} className={`group border p-2 text-left transition duration-150 ${index === themeIndex ? "border-[var(--tw-accent)] bg-[var(--tw-selection)]" : "border-[var(--tw-line)] hover:border-[var(--tw-muted)]"}`} aria-label={`Use ${item.name}`}>
              <div className="mb-2 h-16 border p-2 text-[8px]" style={{ background: item.background, color: item.foreground, borderColor: item.selectionBackground }}><span style={{ color: item.green }}>$</span> vex start<br/><span style={{ color: item.cyan }}>workspace ready</span><div className="mt-2 flex gap-1">{ansiKeys.map((key) => <i key={key} className="size-2 rounded-full" style={{ background: item[key] }} />)}</div></div>
              <span className="text-[10px] lowercase">{item.name}</span>
            </button>)}
          </div>
          <p className="mt-5 text-center text-[10px] text-[var(--tw-muted)]">← → move · Enter keep · Esc revert</p>
        </section>
      </div>
    </main>
  );
}
