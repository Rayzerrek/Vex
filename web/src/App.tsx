import { useState } from "react";
import {
  ArrowDownToLine,
  ArrowUpRight,
  Check,
  ChevronDown,
  ChevronRight,
  Clipboard,
  Command,
  Download,
  FileCode2,
  Folder,
  Github,
  Layers3,
  Menu,
  Monitor,
  PanelLeft,
  PanelTop,
  Plus,
  Search,
  Settings2,
  ShieldCheck,
  SplitSquareHorizontal,
  TerminalSquare,
  X
} from "lucide-react";

const features = [
  {
    icon: SplitSquareHorizontal,
    number: "01",
    title: "Split without losing the plot",
    copy: "Stack terminals, editors, and logs in one view. Every pane stays one shortcut away."
  },
  {
    icon: Folder,
    number: "02",
    title: "Projects, not tabs",
    copy: "Keep multiple repos open with a file tree that knows where you are and remembers your layout."
  },
  {
    icon: Command,
    number: "03",
    title: "Everything has a command",
    copy: "Open the palette with Ctrl + P. Search actions, files, and settings without reaching for the mouse."
  }
];

type DownloadMode = "installer" | "source";

function App() {
  const [menuOpen, setMenuOpen] = useState(false);
  const [downloadMode, setDownloadMode] = useState<DownloadMode>("installer");
  const [copied, setCopied] = useState(false);

  const scrollToDownload = () => {
    document.getElementById("download")?.scrollIntoView({ behavior: "smooth" });
    setMenuOpen(false);
  };

  const copyCommand = async () => {
    await navigator.clipboard.writeText(
      "git clone https://github.com/Rayzerrek/Vex.git"
    );
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1800);
  };

  return (
    <main className="min-h-screen overflow-hidden bg-ink text-paper">
      <div className="pointer-events-none fixed inset-0 z-0 bg-grid opacity-40" />
      <div className="pointer-events-none absolute left-1/2 top-[-22rem] z-0 h-[42rem] w-[42rem] -translate-x-1/2 rounded-full bg-lime/10 blur-[130px]" />

      <nav className="relative z-20 mx-auto flex max-w-[1280px] items-center justify-between px-5 py-5 sm:px-8 lg:px-12">
        <a
          className="flex items-center gap-3"
          href="#top"
          aria-label="Vex home"
        >
          <VexMark />
          <span className="font-display text-lg font-bold tracking-[-0.04em]">
            vex
          </span>
        </a>
        <div className="hidden items-center gap-8 text-[13px] text-mist md:flex">
          <a className="nav-link" href="#features">
            Why Vex
          </a>
          <a className="nav-link" href="#download">
            Download
          </a>
          <a className="nav-link" href="https://github.com/Rayzerrek/Vex">
            GitHub <ArrowUpRight size={13} />
          </a>
        </div>
        <div className="hidden md:block">
          <button className="button button-small" onClick={scrollToDownload}>
            Get Vex <ArrowDownToLine size={14} />
          </button>
        </div>
        <button
          className="rounded-md border border-white/10 p-2 text-mist md:hidden"
          onClick={() => setMenuOpen((open) => !open)}
          aria-label={menuOpen ? "Close menu" : "Open menu"}
          aria-expanded={menuOpen}
        >
          {menuOpen ? <X size={18} /> : <Menu size={18} />}
        </button>
      </nav>
      {menuOpen && (
        <div className="absolute right-5 top-[4.5rem] z-30 flex w-48 flex-col gap-1 rounded-xl border border-white/10 bg-panel p-2 shadow-2xl md:hidden">
          <a
            className="mobile-link"
            href="#features"
            onClick={() => setMenuOpen(false)}
          >
            Why Vex
          </a>
          <a
            className="mobile-link"
            href="#download"
            onClick={() => setMenuOpen(false)}
          >
            Download
          </a>
          <a className="mobile-link" href="https://github.com/Rayzerrek/Vex">
            GitHub <ArrowUpRight size={13} />
          </a>
        </div>
      )}

      <section
        id="top"
        className="relative z-10 mx-auto max-w-[1280px] px-5 pb-20 pt-14 sm:px-8 sm:pt-20 lg:px-12 lg:pb-28 lg:pt-24"
      >
        <div className="grid items-center gap-16 lg:grid-cols-[0.92fr_1.08fr] lg:gap-12">
          <div className="animate-rise">
            <div className="mb-7 flex items-center gap-3 text-[11px] font-semibold uppercase tracking-[0.18em] text-lime">
              <span className="status-dot" />
              Native terminal workspace / Windows
            </div>
            <h1 className="max-w-[700px] font-display text-[clamp(3.65rem,8vw,7.65rem)] font-bold leading-[0.86] tracking-[-0.075em] text-paper">
              Make room
              <br />
              <span className="text-lime">to think.</span>
            </h1>
            <p className="mt-8 max-w-[470px] text-[17px] leading-7 text-mist sm:text-lg">
              Vex is a focused terminal workspace for Windows. Terminals, files,
              and code in one calm, native place.
            </p>
            <div className="mt-9 flex flex-wrap items-center gap-3">
              <button className="button" onClick={scrollToDownload}>
                Download for Windows <ArrowDownToLine size={16} />
              </button>
              <a
                className="button button-quiet"
                href="https://github.com/Rayzerrek/Vex"
              >
                View source <ArrowUpRight size={15} />
              </a>
            </div>
            <div className="mt-10 flex items-center gap-5 text-xs text-dim">
              <span className="flex items-center gap-2">
                <ShieldCheck size={14} className="text-lime" />
                Windows 10 1809+
              </span>
              <span className="h-3 w-px bg-white/15" />
              <span>.NET 8</span>
            </div>
          </div>

          <TerminalPreview />
        </div>
        <div className="mt-16 flex items-center gap-3 text-[10px] uppercase tracking-[0.18em] text-dim lg:mt-24">
          <span className="h-px w-12 bg-lime/60" />
          Scroll to explore
          <ChevronDown size={13} className="animate-bounce" />
        </div>
      </section>

      <section
        id="features"
        className="relative z-10 border-y border-white/10 bg-[#0c100c]/75"
      >
        <div className="mx-auto max-w-[1280px] px-5 py-20 sm:px-8 lg:px-12 lg:py-28">
          <div className="mb-14 flex flex-col justify-between gap-5 lg:flex-row lg:items-end">
            <div>
              <p className="section-kicker">A better default</p>
              <h2 className="mt-4 max-w-[620px] font-display text-4xl font-bold leading-[0.95] tracking-[-0.06em] text-paper sm:text-6xl">
                Your tools should
                <br />
                <span className="text-mist">feel like one tool.</span>
              </h2>
            </div>
            <p className="max-w-[300px] text-sm leading-6 text-dim">
              No browser tabs. No context switching. Just a workspace that gets
              out of the way when the work gets interesting.
            </p>
          </div>
          <div className="grid gap-px overflow-hidden rounded-2xl border border-white/10 bg-white/10 md:grid-cols-3">
            {features.map((feature) => {
              const Icon = feature.icon;
              return (
                <article key={feature.number} className="feature-card">
                  <div className="flex items-start justify-between">
                    <div className="icon-box">
                      <Icon size={19} strokeWidth={1.7} />
                    </div>
                    <span className="font-mono text-xs text-dim">
                      {feature.number}
                    </span>
                  </div>
                  <h3 className="mt-16 font-display text-2xl font-semibold tracking-[-0.045em] text-paper">
                    {feature.title}
                  </h3>
                  <p className="mt-4 text-sm leading-6 text-dim">
                    {feature.copy}
                  </p>
                  <div className="mt-8 flex items-center gap-2 text-xs font-medium text-lime">
                    Explore the workspace <ChevronRight size={14} />
                  </div>
                </article>
              );
            })}
          </div>
        </div>
      </section>

      <section className="relative z-10 mx-auto max-w-[1280px] px-5 py-20 sm:px-8 lg:px-12 lg:py-32">
        <div className="grid items-center gap-14 lg:grid-cols-[0.85fr_1.15fr]">
          <div>
            <p className="section-kicker">Built for flow state</p>
            <h2 className="mt-5 max-w-[530px] font-display text-4xl font-bold leading-[0.94] tracking-[-0.06em] sm:text-6xl">
              Less chrome.
              <br />
              <span className="text-lime">More signal.</span>
            </h2>
            <p className="mt-7 max-w-[430px] leading-7 text-mist">
              Vex is deliberately small and opinionated. A native WPF shell,
              low-latency ConPTY sessions, and a layout that stays yours after
              you close the lid.
            </p>
            <a
              className="mt-8 inline-flex items-center gap-2 text-sm font-semibold text-lime transition hover:gap-3"
              href="#download"
            >
              See what&apos;s inside <ArrowUpRight size={15} />
            </a>
          </div>
          <div className="relative min-h-[390px] overflow-hidden rounded-2xl border border-white/10 bg-panel p-3 shadow-2xl shadow-black/30 sm:p-5">
            <div className="absolute -right-16 -top-16 h-48 w-48 rounded-full bg-lime/10 blur-3xl" />
            <div className="relative h-full min-h-[350px] overflow-hidden rounded-xl border border-white/10 bg-[#111610]">
              <div className="flex h-11 items-center justify-between border-b border-white/10 px-4">
                <div className="flex items-center gap-2">
                  <VexMark small />
                  <span className="font-mono text-[11px] text-mist">
                    vex / workspace
                  </span>
                </div>
                <div className="flex gap-1.5 text-dim">
                  <span className="h-2 w-2 rounded-full bg-white/15" />
                  <span className="h-2 w-2 rounded-full bg-white/15" />
                  <span className="h-2 w-2 rounded-full bg-white/15" />
                </div>
              </div>
              <div className="grid h-[calc(100%-2.75rem)] grid-cols-[150px_1fr]">
                <div className="border-r border-white/10 bg-black/10 p-3 font-mono text-[10px] text-dim">
                  <div className="mb-4 flex items-center justify-between text-[9px] uppercase tracking-widest text-white/30">
                    explorer <Plus size={11} />
                  </div>
                  <div className="flex items-center gap-1.5 text-paper">
                    <ChevronDown size={11} />{" "}
                    <Folder size={12} className="text-lime" /> vex
                  </div>
                  <div className="ml-4 mt-3 space-y-3 border-l border-white/10 pl-3">
                    <div className="flex items-center gap-1.5 text-white/55">
                      <FileCode2 size={11} className="text-sky-300" /> App.xaml
                    </div>
                    <div className="flex items-center gap-1.5 text-lime">
                      <FileCode2 size={11} /> MainWindow.xaml
                    </div>
                    <div className="flex items-center gap-1.5 text-white/55">
                      <FileCode2 size={11} className="text-amber-300" />{" "}
                      README.md
                    </div>
                    <div className="flex items-center gap-1.5 text-white/55">
                      <Folder size={11} /> Assets
                    </div>
                  </div>
                </div>
                <div className="flex flex-col">
                  <div className="flex h-9 items-center gap-5 border-b border-white/10 px-4 font-mono text-[10px] text-dim">
                    <span className="border-b border-lime py-2 text-paper">
                      MainWindow.xaml
                    </span>
                    <span>AppSettings.cs</span>
                    <span className="ml-auto">
                      <Search size={13} />
                    </span>
                  </div>
                  <div className="grid flex-1 grid-rows-[1fr_0.72fr]">
                    <div className="overflow-hidden p-5 font-mono text-[10px] leading-[2] text-white/50">
                      <CodeLine
                        n="01"
                        text='<Window x:Class="Vex.App.MainWindow"'
                      />
                      <CodeLine
                        n="02"
                        text='  Background="{StaticResource VexBackground}">'
                        accent
                      />
                      <CodeLine n="03" text='  <Grid x:Name="MainGrid">' />
                      <CodeLine n="04" text="    <Grid.ColumnDefinitions>" />
                      <CodeLine
                        n="05"
                        text='      <ColumnDefinition Width="240" />'
                        accent
                      />
                      <CodeLine
                        n="06"
                        text='      <ColumnDefinition Width="*" />'
                      />
                      <CodeLine n="07" text="    </Grid.ColumnDefinitions>" />
                      <CodeLine n="08" text="  </Grid>" />
                    </div>
                    <div className="border-t border-white/10 bg-black/20 p-4 font-mono text-[10px] leading-[1.9] text-white/55">
                      <div className="mb-2 flex items-center gap-2 text-white/30">
                        <TerminalSquare size={12} /> TERMINAL · pwsh
                      </div>
                      <div>
                        <span className="text-lime">PS</span>{" "}
                        <span className="text-sky-300">C:\Projects\vex</span>{" "}
                        <span className="text-paper">&gt;</span> dotnet build
                      </div>
                      <div className="text-lime">
                        Build succeeded. 0 Warning(s) 0 Error(s)
                      </div>
                      <div className="mt-1 flex items-center gap-1 text-white/35">
                        <span>ready</span>
                        <span className="cursor-block" />
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </section>

      <section
        id="download"
        className="relative z-10 scroll-mt-5 bg-lime text-ink"
      >
        <div className="mx-auto max-w-[1280px] px-5 py-20 sm:px-8 lg:px-12 lg:py-28">
          <div className="grid gap-12 lg:grid-cols-[0.9fr_1.1fr] lg:gap-24">
            <div>
              <div className="mb-6 flex items-center gap-2 text-[11px] font-bold uppercase tracking-[0.18em] text-ink/60">
                <Download size={14} />
                Start here
              </div>
              <h2 className="max-w-[560px] font-display text-5xl font-bold leading-[0.88] tracking-[-0.07em] sm:text-7xl">
                Your next
                <br />
                workspace.
              </h2>
              <p className="mt-7 max-w-[390px] text-[15px] leading-6 text-ink/70">
                Vex is free, open source, and built for Windows. Download the
                latest installer or clone the repo and make it yours.
              </p>
              <div className="mt-8 flex flex-wrap gap-3 text-xs font-semibold text-ink/65">
                <span className="rounded-full border border-ink/20 px-3 py-1.5">
                  Windows 10 / 11
                </span>
                <span className="rounded-full border border-ink/20 px-3 py-1.5">
                  x64
                </span>
                <span className="rounded-full border border-ink/20 px-3 py-1.5">
                  Open source
                </span>
              </div>
            </div>
            <div className="rounded-2xl border border-ink/15 bg-ink p-2 text-paper shadow-2xl shadow-ink/20 sm:p-3">
              <div className="flex gap-1 rounded-xl bg-panel p-1">
                <button
                  className={`download-tab ${downloadMode === "installer" ? "download-tab-active" : ""}`}
                  onClick={() => setDownloadMode("installer")}
                >
                  <Monitor size={15} /> Windows installer
                </button>
                <button
                  className={`download-tab ${downloadMode === "source" ? "download-tab-active" : ""}`}
                  onClick={() => setDownloadMode("source")}
                >
                  <Github size={15} /> Build from source
                </button>
              </div>
              {downloadMode === "installer" ? (
                <div className="p-6 sm:p-9">
                  <div className="flex items-center gap-3">
                    <div className="flex h-11 w-11 items-center justify-center rounded-xl bg-lime text-ink">
                      <VexMark small dark />
                    </div>
                    <div>
                      <h3 className="font-display text-xl font-semibold tracking-[-0.04em]">
                        Vex for Windows
                      </h3>
                      <p className="mt-0.5 font-mono text-[11px] text-dim">
                        Latest · v0.1.0 · x64
                      </p>
                    </div>
                  </div>
                  <div className="my-8 h-px bg-white/10" />
                  <div className="flex flex-col justify-between gap-5 sm:flex-row sm:items-center">
                    <div className="flex items-center gap-2 text-sm text-mist">
                      <ShieldCheck size={16} className="text-lime" /> Signed
                      installer
                    </div>
                    <a
                      className="button button-wide"
                      href="https://github.com/Rayzerrek/Vex/releases"
                      target="_blank"
                      rel="noreferrer"
                    >
                      Download .exe <ArrowDownToLine size={16} />
                    </a>
                  </div>
                  <p className="mt-6 text-center font-mono text-[10px] text-dim">
                    No account required · MIT licensed
                  </p>
                </div>
              ) : (
                <div className="p-6 sm:p-9">
                  <div className="flex items-center gap-3">
                    <div className="flex h-11 w-11 items-center justify-center rounded-xl border border-white/10 bg-white/5 text-lime">
                      <TerminalSquare size={20} />
                    </div>
                    <div>
                      <h3 className="font-display text-xl font-semibold tracking-[-0.04em]">
                        Make a local build
                      </h3>
                      <p className="mt-0.5 font-mono text-[11px] text-dim">
                        .NET 8 SDK · Visual Studio 2022
                      </p>
                    </div>
                  </div>
                  <div className="my-8 rounded-lg border border-white/10 bg-black/25 p-4 font-mono text-xs text-mist">
                    <span className="text-lime">$</span> git clone
                    https://github.com/Rayzerrek/Vex.git
                  </div>
                  <div className="flex flex-col justify-between gap-5 sm:flex-row sm:items-center">
                    <div className="flex items-center gap-2 text-sm text-mist">
                      <Layers3 size={16} className="text-lime" /> Fully open
                      source
                    </div>
                    <button
                      className="button button-wide"
                      onClick={copyCommand}
                    >
                      {copied ? <Check size={16} /> : <Clipboard size={16} />}
                      {copied ? "Copied" : "Copy command"}
                    </button>
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>
      </section>

      <footer className="relative z-10 mx-auto flex max-w-[1280px] flex-col gap-7 px-5 py-10 text-xs text-dim sm:px-8 md:flex-row md:items-center md:justify-between lg:px-12">
        <div className="flex items-center gap-3">
          <VexMark small />
          <span>Vex, a native terminal workspace.</span>
        </div>
        <div className="flex items-center gap-6">
          <a className="footer-link" href="https://github.com/Rayzerrek/Vex">
            GitHub <ArrowUpRight size={12} />
          </a>
          <span>Built for Windows</span>
          <span>© 2026</span>
        </div>
      </footer>
    </main>
  );
}

function VexMark({
  small = false,
  dark = false
}: {
  small?: boolean;
  dark?: boolean;
}) {
  return (
    <span
      className={`vex-mark ${small ? "vex-mark-small" : ""} ${dark ? "vex-mark-dark" : ""}`}
      aria-hidden="true"
    >
      <span />
      <span />
    </span>
  );
}

function CodeLine({
  n,
  text,
  accent = false
}: {
  n: string;
  text: string;
  accent?: boolean;
}) {
  return (
    <div className="flex gap-4">
      <span className="select-none text-white/20">{n}</span>
      <span className={accent ? "text-lime/75" : ""}>{text}</span>
    </div>
  );
}

function TerminalPreview() {
  return (
    <div className="terminal-wrap animate-rise-delay">
      <div className="terminal-glow" />
      <div className="terminal-window">
        <div className="terminal-titlebar">
          <div className="flex items-center gap-2">
            <VexMark small />
            <span className="font-mono text-[10px] text-mist">
              vex — workspace
            </span>
          </div>
          <div className="flex gap-1.5">
            <span className="window-dot bg-white/20" />
            <span className="window-dot bg-white/20" />
            <span className="window-dot bg-white/20" />
          </div>
        </div>
        <div className="flex h-[345px] sm:h-[405px]">
          <div className="hidden w-[145px] shrink-0 border-r border-white/10 bg-black/10 p-3 sm:block">
            <div className="mb-5 flex items-center justify-between font-mono text-[9px] uppercase tracking-[0.15em] text-white/30">
              Projects <Plus size={12} />
            </div>
            <div className="project-item project-item-active">
              <span className="h-1.5 w-1.5 rounded-full bg-lime" /> vex
            </div>
            <div className="project-item">
              <span className="h-1.5 w-1.5 rounded-full bg-sky-300" /> kero
            </div>
            <div className="project-item">
              <span className="h-1.5 w-1.5 rounded-full bg-amber-300" />{" "}
              dotfiles
            </div>
            <div className="mt-7 border-t border-white/10 pt-4 font-mono text-[9px] uppercase tracking-[0.15em] text-white/30">
              Explorer
            </div>
            <div className="mt-4 space-y-3 font-mono text-[10px] text-white/45">
              <div className="flex items-center gap-2 text-white/75">
                <ChevronDown size={11} />
                <Folder size={12} className="text-lime" /> src
              </div>
              <div className="ml-5 flex items-center gap-2">
                <FileCode2 size={11} /> App.xaml.cs
              </div>
              <div className="ml-5 flex items-center gap-2 text-lime">
                <FileCode2 size={11} /> MainWindow.xaml
              </div>
              <div className="flex items-center gap-2">
                <ChevronRight size={11} />
                <Folder size={12} /> Assets
              </div>
            </div>
          </div>
          <div className="flex min-w-0 flex-1 flex-col">
            <div className="flex h-10 items-center gap-5 border-b border-white/10 px-4 font-mono text-[10px] text-dim">
              <span className="flex h-full items-center gap-2 border-b border-lime text-paper">
                <FileCode2 size={12} className="text-sky-300" /> MainWindow.xaml{" "}
                <X size={11} />
              </span>
              <span className="hidden items-center gap-2 sm:flex">
                <FileCode2 size={12} className="text-amber-300" />{" "}
                AppSettings.cs
              </span>
              <span className="ml-auto">
                <Search size={13} />
              </span>
            </div>
            <div className="flex flex-1 flex-col">
              <div className="flex-1 overflow-hidden p-4 font-mono text-[9px] leading-[2.1] text-white/50 sm:p-6 sm:text-[10px]">
                <CodeLine n="01" text='<Window x:Class="Vex.App.MainWindow"' />
                <CodeLine
                  n="02"
                  text='  xmlns="http://schemas.microsoft.com/"'
                />
                <CodeLine
                  n="03"
                  text='  Background="{StaticResource VexBackground}">'
                  accent
                />
                <CodeLine n="04" text='  <Grid x:Name="MainGrid">' />
                <CodeLine n="05" text="    <Grid.ColumnDefinitions>" />
                <CodeLine
                  n="06"
                  text='      <ColumnDefinition Width="240" />'
                  accent
                />
                <CodeLine n="07" text='      <ColumnDefinition Width="*" />' />
                <CodeLine n="08" text="    </Grid.ColumnDefinitions>" />
                <CodeLine
                  n="09"
                  text='    <local:SplitPaneView Pane="{Binding}" />'
                />
                <CodeLine n="10" text="  </Grid>" />
                <CodeLine n="11" text="</Window>" accent />
              </div>
              <div className="h-[115px] border-t border-white/10 bg-black/20 p-4 font-mono text-[9px] leading-[1.9] text-white/55 sm:h-[130px] sm:p-5 sm:text-[10px]">
                <div className="mb-2 flex items-center gap-2 text-white/30">
                  <TerminalSquare size={12} /> TERMINAL · pwsh
                </div>
                <div>
                  <span className="text-lime">PS</span>{" "}
                  <span className="text-sky-300">C:\Projects\vex</span>{" "}
                  <span className="text-paper">&gt;</span> dotnet run
                </div>
                <div className="text-lime">Vex workspace ready.</div>
                <div className="mt-1 flex items-center gap-1 text-white/35">
                  <span>▌</span>
                  <span className="cursor-block" />
                </div>
              </div>
            </div>
          </div>
        </div>
        <div className="flex items-center justify-between border-t border-white/10 bg-black/15 px-4 py-2 font-mono text-[9px] text-white/30">
          <span className="flex items-center gap-2">
            <span className="h-1.5 w-1.5 rounded-full bg-lime" /> 1 session
          </span>
          <span className="flex items-center gap-3">
            <PanelLeft size={11} /> <PanelTop size={11} />{" "}
            <Settings2 size={11} />
          </span>
        </div>
      </div>
    </div>
  );
}

export default App;
