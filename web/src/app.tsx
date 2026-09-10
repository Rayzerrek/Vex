import { useEffect, useState } from "react";
import { Link } from "@tanstack/react-router";

const frame = "mx-auto w-[min(100%_-_48px,640px)]";

const sectionTitle =
  "mt-20 border-b border-line pb-3 font-mono text-[11px] tracking-[0.08em] text-faint";

const rowLink =
  "group flex items-baseline gap-4 border-b border-line py-4 no-underline transition-colors duration-150 hover:bg-surface focus-visible:bg-surface";

const downloadOptions = [
  {
    href: "https://github.com/Rayzerrek/Vex/releases",
    title: "Windows installer",
    detail: "Latest release from GitHub",
    meta: "exe",
  },
  {
    href: "https://github.com/Rayzerrek/Vex",
    title: "Build from source",
    detail: "MIT licensed and open to inspection",
    meta: "git",
  },
];

const principles = [
  {
    title: "Native first",
    copy: "WPF and Windows ConPTY give Vex the feel of a desktop tool, not a browser inside a window.",
    meta: "wpf",
  },
  {
    title: "Projects stay open",
    copy: "Switch between repositories without losing the shell, files, or layout that belongs to each one.",
    meta: "workspace",
  },
  {
    title: "Shortcuts are the interface",
    copy: "Split panes, open files, and move through the workspace without breaking your focus.",
    meta: "keyboard",
  },
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
      // Storage unavailable; theme still applies for this visit.
    }
  };

  return { dark, toggle };
}

export function HomePage() {
  return (
    <div className="min-h-dvh bg-background text-text">
      <SiteHeader />
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
            <Link
              className="underline decoration-line underline-offset-4 transition-colors hover:decoration-text"
              to="/download"
            >
              Download
            </Link>
            <a
              className="underline decoration-line underline-offset-4 transition-colors hover:decoration-text"
              href="https://github.com/Rayzerrek/Vex"
            >
              GitHub
            </a>
            <a
              className="underline decoration-line underline-offset-4 transition-colors hover:decoration-text"
              href="/#workspace"
            >
              Workspace
            </a>
          </p>
        </section>

        <figure className="mt-10">
          <div className="overflow-hidden rounded-xl border border-line bg-surface shadow-[0_1px_2px_rgba(25,25,24,0.06),0_24px_48px_-24px_rgba(25,25,24,0.25)] dark:shadow-[0_1px_2px_rgba(0,0,0,0.4),0_24px_48px_-24px_rgba(0,0,0,0.7)]">
            <img
              className="block w-full dark:hidden"
              src="/shots/vex-light.png"
              alt="Vex on Windows with the sidebar open, a file tree, and a Nushell terminal listing the project"
              loading="eager"
            />
            <img
              className="hidden w-full dark:block"
              src="/shots/vex-dark.png"
              alt="Vex on Windows in dark appearance with the sidebar open, a file tree, and a Nushell terminal listing the project"
              loading="eager"
            />
          </div>
          <figcaption className="mt-3 font-mono text-[11px] tracking-[0.04em] text-faint">
            vex on windows — sidebar, file tree, nushell. follows this page
            into dark.
          </figcaption>
        </figure>

        <section id="workspace">
          <h2 className={sectionTitle}>Workspace</h2>
          <div>
            {principles.map(({ title, copy, meta }) => (
              <article key={title} className="border-b border-line py-4">
                <div className="flex items-baseline justify-between gap-4">
                  <h3 className="text-[15px] font-medium tracking-[-0.01em]">
                    {title}
                  </h3>
                  <span className="shrink-0 font-mono text-[11px] text-faint">
                    {meta}
                  </span>
                </div>
                <p className="mt-1.5 max-w-[520px] text-[14px] leading-[1.7] text-muted">
                  {copy}
                </p>
              </article>
            ))}
          </div>
          <figure className="mt-8">
            <div className="overflow-hidden rounded-xl border border-line bg-surface shadow-[0_1px_2px_rgba(25,25,24,0.06),0_24px_48px_-24px_rgba(25,25,24,0.25)] dark:shadow-[0_1px_2px_rgba(0,0,0,0.4),0_24px_48px_-24px_rgba(0,0,0,0.7)]">
              <img
                className="block w-full"
                src="/shots/vex-palette.png"
                alt="Vex command palette listing New Tab, Split Right, Split Down, and project switching"
                loading="lazy"
              />
            </div>
            <figcaption className="mt-3 font-mono text-[11px] tracking-[0.04em] text-faint">
              command palette — ctrl+shift+p. every action, one keystroke
              away.
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
              { term: "License", detail: "MIT" },
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
          <div>
            {downloadOptions.map(({ href, title, detail, meta }) => (
              <a
                className={rowLink}
                href={href}
                target="_blank"
                rel="noreferrer"
                key={href}
              >
                <span className="min-w-0 flex-1">
                  <span className="block text-[15px] font-medium tracking-[-0.01em]">
                    {title}
                  </span>
                  <span className="mt-1 block text-[14px] leading-[1.6] text-muted">
                    {detail}
                  </span>
                </span>
                <span className="shrink-0 font-mono text-[11px] text-faint transition-colors group-hover:text-text">
                  {meta} →
                </span>
              </a>
            ))}
          </div>
        </section>
      </main>
      <SiteFooter />
    </div>
  );
}

export function DownloadPage() {
  return (
    <div className="min-h-dvh bg-background text-text">
      <SiteHeader />
      <main className={`${frame} pb-24 pt-16 max-narrow:pt-12`}>
        <h1 className="max-w-[560px] text-[32px] font-medium leading-[1.25] tracking-[-0.02em] max-narrow:text-[28px]">
          Start with Vex.
        </h1>
        <p className="mt-5 max-w-[520px] text-[15px] leading-[1.7] text-muted">
          A native terminal workspace for Windows developers. Pick the
          installer or build it yourself — both are MIT licensed.
        </p>
        <div className="mt-8 border-t border-line">
          {downloadOptions.map(({ href, title, detail, meta }) => (
            <a
              className={rowLink}
              href={href}
              target="_blank"
              rel="noreferrer"
              key={href}
            >
              <span className="min-w-0 flex-1">
                <span className="block text-[15px] font-medium tracking-[-0.01em]">
                  {title}
                </span>
                <span className="mt-1 block text-[14px] leading-[1.6] text-muted">
                  {detail}
                </span>
              </span>
              <span className="shrink-0 font-mono text-[11px] text-faint transition-colors group-hover:text-text">
                {meta} →
              </span>
            </a>
          ))}
        </div>
        <p className="mt-8 text-[14px]">
          <Link
            className="underline decoration-line underline-offset-4 transition-colors hover:decoration-text"
            to="/"
          >
            Back to Vex
          </Link>
        </p>
      </main>
      <SiteFooter />
    </div>
  );
}

function SiteHeader() {
  const { dark, toggle } = useTheme();

  return (
    <header className={`${frame} flex h-14 items-center justify-between`}>
      <Link
        className="flex items-center gap-2 text-[15px] font-semibold tracking-[-0.02em] no-underline"
        to="/"
        aria-label="Vex home"
      >
        <Mark />
        <span>vex</span>
      </Link>
      <nav
        className="flex items-center gap-5 text-[14px] text-muted"
        aria-label="Main navigation"
      >
        <a
          className="no-underline transition-colors duration-150 hover:text-text max-narrow:hidden"
          href="/#workspace"
        >
          workspace
        </a>
        <Link
          className="no-underline transition-colors duration-150 hover:text-text"
          to="/download"
        >
          download
        </Link>
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
                : "linear-gradient(90deg, currentColor 50%, transparent 50%)",
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
      <span className="font-mono text-[11px] tracking-[0.04em]">
        native terminal workspace
      </span>
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
