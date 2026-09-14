import { useEffect, useState } from "react";

const frame = "mx-auto w-[min(100%_-_48px,640px)]";

const sectionTitle =
  "mt-20 border-b border-line pb-3 font-mono text-[11px] tracking-[0.08em] text-faint";

const downloadOptions = [
  {
    href: "https://github.com/Rayzerrek/Vex/releases",
    title: "Windows installer",
    detail: "Latest release from GitHub",
    meta: "exe"
  },
  {
    href: "https://github.com/Rayzerrek/Vex",
    title: "Build from source",
    detail: "GPL-3.0 licensed and open to inspection",
    meta: "git"
  }
];

const principles = [
  {
    title: "Native first",
    copy: "WPF and Windows ConPTY give Vex the feel of a desktop tool, not a browser inside a window.",
    meta: "wpf"
  },
  {
    title: "Projects stay open",
    copy: "Switch between repositories without losing the shell, files, or layout that belongs to each one.",
    meta: "workspace"
  },
  {
    title: "Shortcuts are the interface",
    copy: "Split panes, open files, and move through the workspace without breaking your focus.",
    meta: "keyboard"
  }
];

function useTheme() {
  const [dark, setDark] = useState(false);

  useEffect(() => {
    setDark(document.documentElement.classList.contains("dark"));
  }, []);

  const toggle = () => {
    const next = !dark;
    setDark(next);
    document.documentElement.classList.toggle("dark", next);
    try {
      localStorage.setItem("vex-theme", next ? "dark" : "light");
    } catch {
      // xd
    }
  };

  return { dark, toggle };
}

export function HomePage() {
  const { dark, toggle } = useTheme();

  return (
    <div className="min-h-dvh bg-background text-text">
      <SiteHeader dark={dark} toggle={toggle} />
      <main className={`${frame} pb-24`}>
        <section className="rise pt-16 max-narrow:pt-12" id="top">
          <h1 className="max-w-[560px] text-[32px] font-medium leading-[1.25] tracking-[-0.02em] max-narrow:text-[28px]">
            Vex is a native terminal workspace for Windows.
          </h1>
          <p className="mt-5 max-w-[520px] text-[15px] leading-[1.7] text-muted">
            It brings terminals, files, and a quiet editor into one window —
            built around ConPTY, split panes, and projects that stay open.
          </p>
          <p className="mt-5 flex flex-wrap gap-x-5 gap-y-2 text-[14px]">
            <a
              className="underline decoration-line underline-offset-4 transition-colors hover:decoration-text"
              href="/#download"
            >
              Download
            </a>
            <a
              className="underline decoration-line underline-offset-4 transition-colors hover:decoration-text"
              href="https://github.com/Rayzerrek/Vex"
            >
              GitHub
            </a>
            <a
              className="underline decoration-line underline-offset-4 transition-colors hover:decoration-text"
              href="/#features"
            >
              Features
            </a>
          </p>
        </section>

        <figure className="mt-10">
          <div className="overflow-hidden rounded-xl border border-line bg-surface shadow-[0_1px_2px_rgba(25,25,24,0.06),0_24px_48px_-24px_rgba(25,25,24,0.25)] dark:shadow-[0_1px_2px_rgba(0,0,0,0.4),0_24px_48px_-24px_rgba(0,0,0,0.7)]">
            <img
              className="block aspect-[16/9] w-full object-cover object-top"
              src={dark ? "/shots/vex-dark.png" : "/shots/vex-light.png"}
              alt="Vex on Windows with the sidebar open, a file tree, and a terminal listing the project"
              loading="eager"
              fetchPriority="high"
              decoding="async"
            />
          </div>
          <figcaption className="mt-3 font-mono text-[11px] tracking-[0.04em] text-faint">
            vex on windows — sidebar, file tree, terminal.
          </figcaption>
        </figure>

        <section id="features">
          <h2 className="mt-20 max-w-[500px] text-[22px] font-medium leading-[1.35] tracking-[-0.02em]">
            Everything stays in reach.
          </h2>
          <p className="mt-3 max-w-[500px] text-[14px] leading-[1.7] text-muted">
            Vex keeps the parts of terminal work that usually get scattered
            across windows in one quiet, keyboard-first surface.
          </p>
          <div>
            {principles.map(({ title, copy, meta }, index) => (
              <article
                key={title}
                className="grid grid-cols-[32px_1fr_auto] gap-4 border-b border-line py-6 max-narrow:grid-cols-[24px_1fr] max-narrow:gap-3"
              >
                <span className="font-mono text-[11px] text-faint">
                  {String(index + 1).padStart(2, "0")}
                </span>
                <div>
                  <h3 className="text-[15px] font-medium tracking-[-0.01em]">
                    {title}
                  </h3>
                  <p className="mt-2 max-w-[520px] text-[14px] leading-[1.7] text-muted">
                    {copy}
                  </p>
                </div>
                <span className="pt-0.5 font-mono text-[10px] uppercase tracking-[0.12em] text-faint max-narrow:col-start-2 max-narrow:row-start-1 max-narrow:justify-self-end">
                  {meta}
                </span>
              </article>
            ))}
          </div>
          <figure className="mt-12">
            <div className="overflow-hidden rounded-xl border border-line bg-surface shadow-[0_1px_2px_rgba(25,25,24,0.06),0_24px_48px_-24px_rgba(25,25,24,0.25)] dark:shadow-[0_1px_2px_rgba(0,0,0,0.4),0_24px_48px_-24px_rgba(0,0,0,0.7)]">
              <img
                className="block w-full"
                src="/shots/vex-palette.png"
                alt="Vex command palette listing New Tab, Split Right, Split Down, and project switching"
                loading="lazy"
              />
            </div>
            <figcaption className="mt-3 font-mono text-[11px] tracking-[0.04em] text-faint">
              command palette — ctrl+shift+p. every action, one keystroke away.
            </figcaption>
          </figure>
        </section>

        <section>
          <h2 className={sectionTitle}>Details</h2>
          <pre
            className="m-0 overflow-auto border-b border-line py-4 font-mono text-[12px] leading-[1.9] text-muted"
            aria-label="Vex architecture"
          >
            <code>{`windows/Vex.App
  shell       WPF window and workspace state
  terminal    ConPTY sessions and input
  editor      syntax-aware file editing
  explorer    project files and folders`}</code>
          </pre>
          <dl className="grid grid-cols-3 gap-4 max-narrow:grid-cols-1 max-narrow:gap-0">
            {[
              { term: "Platform", detail: "Windows 10 1809+" },
              { term: "Runtime", detail: ".NET 10" },
              { term: "License", detail: "GPL-3.0" }
            ].map(({ term, detail }) => (
              <div
                className="border-b border-line py-4 max-narrow:py-3"
                key={term}
              >
                <dt className="font-mono text-[11px] tracking-[0.06em] text-faint">
                  {term}
                </dt>
                <dd className="mt-1.5 font-mono text-[12px] text-text">
                  {detail}
                </dd>
              </div>
            ))}
          </dl>
        </section>

        <section id="download">
          <h2 className={sectionTitle}>Download</h2>
          <DownloadChoices compact />
        </section>
      </main>
      <SiteFooter />
    </div>
  );
}

function DownloadChoices({ compact = false }: { compact?: boolean }) {
  return (
    <div
      className={`${compact ? "mt-2" : "mt-8"} grid grid-cols-2 gap-3 max-narrow:grid-cols-1`}
    >
      {downloadOptions.map(({ href, title, detail, meta }) => (
        <a
          className="group flex min-h-[164px] flex-col border border-line bg-surface p-5 no-underline transition-colors duration-150 hover:border-faint max-narrow:min-h-0 max-narrow:p-4"
          href={href}
          target="_blank"
          rel="noreferrer"
          key={href}
        >
          <div className="flex items-start justify-between gap-4">
            <h3 className="text-[17px] font-medium tracking-[-0.02em]">
              {title}
            </h3>
            <span className="font-mono text-[16px] text-faint transition-transform duration-150 group-hover:translate-x-1 group-hover:text-text">
              ↗
            </span>
          </div>
          <p className="mt-2 flex-1 text-[14px] leading-[1.6] text-muted">
            {detail}
          </p>
          <span className="mt-5 border-t border-line pt-3 font-mono text-[11px] text-faint">
            {meta}
          </span>
        </a>
      ))}
    </div>
  );
}

function SiteHeader({ dark, toggle }: { dark: boolean; toggle: () => void }) {
  return (
    <header className={`${frame} flex h-14 items-center justify-between`}>
      <a
        className="flex items-center gap-2 text-[15px] font-semibold tracking-[-0.02em] no-underline"
        href="/"
        aria-label="Vex home"
      >
        <Mark />
        <span>vex</span>
      </a>
      <nav
        className="flex items-center gap-5 text-[14px] text-muted"
        aria-label="Main navigation"
      >
        <a
          className="no-underline transition-colors duration-150 hover:text-text max-narrow:hidden"
          href="/#features"
        >
          features
        </a>
        <a
          className="no-underline transition-colors duration-150 hover:text-text"
          href="/#download"
        >
          download
        </a>
        <a
          className="no-underline transition-colors duration-150 hover:text-text"
          href="https://github.com/Rayzerrek/Vex"
        >
          github
        </a>
        <button
          type="button"
          onClick={toggle}
          aria-label={dark ? "Switch to light theme" : "Switch to dark theme"}
          aria-pressed={dark}
          className="flex size-7 cursor-pointer items-center justify-center rounded-full border border-line bg-transparent text-muted transition-colors duration-150 hover:border-faint hover:text-text"
        >
          <span
            className="block size-3.5 rounded-full border border-current"
            aria-hidden="true"
            style={{
              background: dark
                ? "linear-gradient(90deg, transparent 50%, currentColor 50%)"
                : "linear-gradient(90deg, currentColor 50%, transparent 50%)"
            }}
          />
        </button>
      </nav>
    </header>
  );
}

function SiteFooter() {
  return (
    <footer
      className={`${frame} flex items-center justify-between border-t border-line py-5 text-[13px] text-faint max-narrow:flex-col max-narrow:items-start max-narrow:gap-1`}
    >
      <span>© 2026 Vex.</span>
    </footer>
  );
}

function Mark() {
  return (
    <span
      className="relative inline-block size-[16px] skew-x-[-14deg]"
      aria-hidden="true"
    >
      <span className="absolute bottom-0 left-[3px] block h-[11px] w-[4px] bg-text" />
      <span className="absolute bottom-0 right-[3px] block h-[16px] w-[4px] bg-text" />
    </span>
  );
}
