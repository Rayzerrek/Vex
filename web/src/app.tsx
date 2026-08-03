import { Link } from "@tanstack/react-router";
import { TerminalDemo } from "./terminal-demo.tsx";

const frame =
  "mx-auto w-[min(100%_-_48px,1180px)] max-narrow:w-[min(100%_-_32px,1180px)]";

const eyebrow =
  "mb-6 font-mono text-[10px] lowercase tracking-[0.08em] text-accent";

const textLink =
  "font-mono text-xs text-text no-underline transition-colors duration-150 hover:text-accent focus-visible:text-accent";

export function HomePage() {
  return (
    <div className="min-h-dvh overflow-hidden bg-background text-text">
      <SiteHeader />
      <main>
        <section
          className={`${frame} grid min-h-[calc(100dvh-72px)] items-center gap-18 pt-18 pb-24 grid-cols-[minmax(0,0.82fr)_minmax(0,1.18fr)] max-wide:min-h-auto max-wide:grid-cols-1 max-wide:gap-14 max-narrow:pt-16 max-narrow:pb-20`}
          id="top"
        >
          <div className="max-w-[470px]">
            <p className={eyebrow}>native terminal workspace</p>
            <h1 className="max-w-[520px] text-[clamp(3.25rem,7vw,6rem)] leading-[0.94] tracking-[-0.055em] max-narrow:text-[clamp(3rem,15vw,4.8rem)]">
              Your terminal. In its place.
            </h1>
            <p className="mt-7 max-w-[340px] text-base leading-[1.65] text-muted">
              A focused Windows workspace for terminals, files, and code.
            </p>
            <div className="mt-9 flex flex-wrap items-center gap-6">
              <Link
                className="inline-flex min-h-[42px] items-center justify-center rounded-[4px] border border-transparent bg-accent px-4.5 font-mono text-xs text-background no-underline transition duration-150 active:scale-[0.98] hover:bg-accent-strong focus-visible:bg-accent-strong"
                to="/download"
              >
                download
              </Link>
              <a className={textLink} href="https://github.com/Rayzerrek/Vex">
                view source{" "}
                <span className="ml-1.75 text-accent" aria-hidden="true">
                  -&gt;
                </span>
              </a>
            </div>
          </div>
          <TerminalDemo />
        </section>

        <section
          className={`${frame} border-t border-line pb-24 pt-40 max-narrow:py-28`}
          id="workspace"
        >
          <p className={eyebrow}>the workspace</p>
          <h2 className="max-w-[720px] text-[clamp(2.25rem,4.5vw,4rem)] leading-[0.98] tracking-[-0.055em]">
            Everything you need. Nothing in the way.
          </h2>
          <p className="mt-6 max-w-[390px] text-base leading-[1.65] text-muted">
            Vex keeps the useful parts of a terminal workspace close and the
            interface quiet.
          </p>
        </section>

        <section
          className={`${frame} grid grid-cols-3 border-t border-line max-narrow:grid-cols-1`}
          aria-label="Principles"
        >
          <Principle
            title="native first"
            copy="WPF and Windows ConPTY give Vex the feel of a desktop tool, not a browser inside a window."
          />
          <Principle
            title="projects stay open"
            copy="Switch between repositories without losing the shell, files, or layout that belongs to each one."
          />
          <Principle
            title="shortcuts are the interface"
            copy="Split panes, open files, and move through the workspace without breaking your focus."
          />
        </section>

        <section
          className={`${frame} grid grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)] gap-18 border-t border-line py-40 max-wide:grid-cols-1 max-wide:gap-14 max-narrow:py-28`}
        >
          <div>
            <p className={eyebrow}>under the surface</p>
            <h2 className="max-w-[650px] text-[clamp(2.25rem,4.5vw,4rem)] leading-[0.98] tracking-[-0.055em]">
              Small pieces. Clear boundaries.
            </h2>
            <p className="mt-6 max-w-[350px] text-base leading-[1.65] text-muted">
              Vex is built around the work a terminal needs to do well: start
              quickly, render reliably, and stay out of the way.
            </p>
          </div>
          <div className="min-w-0">
            <pre
              className="m-0 overflow-auto border border-line bg-surface p-5.5 font-mono text-[11px] leading-[1.9] text-muted"
              aria-label="Vex architecture"
            >
              <code>{`windows/Vex.App
  shell             WPF window and workspace state
  terminal          ConPTY sessions and input
  editor            syntax-aware file editing
  explorer          project files and folders

Vendor/XtermSharp   terminal emulation`}</code>
            </pre>
            <dl className="mt-7 grid grid-cols-3 gap-5 max-narrow:grid-cols-1 max-narrow:gap-0">
              <div className="border-t border-line pt-3 max-narrow:py-3.5">
                <dt className="font-mono text-[10px] lowercase tracking-[0.08em] text-faint">
                  platform
                </dt>
                <dd className="mt-2 font-mono text-[11px] text-text">
                  Windows 10 1809+
                </dd>
              </div>
              <div className="border-t border-line pt-3 max-narrow:py-3.5">
                <dt className="font-mono text-[10px] lowercase tracking-[0.08em] text-faint">
                  runtime
                </dt>
                <dd className="mt-2 font-mono text-[11px] text-text">.NET 8</dd>
              </div>
              <div className="border-t border-line pt-3 max-narrow:py-3.5">
                <dt className="font-mono text-[10px] lowercase tracking-[0.08em] text-faint">
                  license
                </dt>
                <dd className="mt-2 font-mono text-[11px] text-text">MIT</dd>
              </div>
            </dl>
          </div>
        </section>

        <section
          className={`${frame} grid grid-cols-[minmax(0,1fr)_minmax(0,0.7fr)] gap-18 border-t border-line pb-[120px] pt-20 max-wide:grid-cols-1 max-wide:gap-8`}
          id="download"
        >
          <div>
            <p className={eyebrow}>get started</p>
            <h2 className="max-w-[650px] text-[clamp(2.25rem,4.5vw,4rem)] leading-[0.98] tracking-[-0.055em]">
              Open a better default.
            </h2>
          </div>
          <div className="max-w-[280px] self-end">
            <p className="text-base leading-[1.65] text-muted">
              Download the latest Windows installer or build Vex from source.
            </p>
            <Link className={`${textLink} mt-6 inline-block`} to="/download">
              choose a download{" "}
              <span className="ml-1.75 text-accent" aria-hidden="true">
                -&gt;
              </span>
            </Link>
          </div>
        </section>
      </main>
      <SiteFooter />
    </div>
  );
}

export function DownloadPage() {
  return (
    <div className="min-h-dvh overflow-hidden bg-background text-text">
      <SiteHeader />
      <main
        className={`${frame} min-h-[calc(100dvh-144px)] pt-28 pb-[150px] max-narrow:min-h-[calc(100dvh-154px)] max-narrow:pt-20 max-narrow:pb-25`}
      >
        <p className={eyebrow}>download</p>
        <h1 className="max-w-[520px] text-[clamp(3.25rem,7vw,6rem)] leading-[0.94] tracking-[-0.055em] max-narrow:text-[clamp(3rem,15vw,4.8rem)]">
          Start with Vex.
        </h1>
        <p className="mt-6 max-w-[360px] text-base leading-[1.65] text-muted">
          A native terminal workspace for Windows developers.
        </p>
        <div className="mb-10.5 mt-16 max-w-[720px] border-t border-line">
          <a
            className="group flex items-center justify-between gap-5 border-b border-line py-5.5 text-text no-underline"
            href="https://github.com/Rayzerrek/Vex/releases"
            target="_blank"
            rel="noreferrer"
          >
            <span>
              <strong className="block font-mono text-[13px] font-normal transition-colors duration-150 group-hover:text-accent group-focus-visible:text-accent">
                windows installer
              </strong>
              <small className="mt-1.75 block text-[13px] text-muted">
                latest release from GitHub
              </small>
            </span>
            <span className="font-mono text-accent" aria-hidden="true">
              -&gt;
            </span>
          </a>
          <a
            className="group flex items-center justify-between gap-5 border-b border-line py-5.5 text-text no-underline"
            href="https://github.com/Rayzerrek/Vex"
            target="_blank"
            rel="noreferrer"
          >
            <span>
              <strong className="block font-mono text-[13px] font-normal transition-colors duration-150 group-hover:text-accent group-focus-visible:text-accent">
                build from source
              </strong>
              <small className="mt-1.75 block text-[13px] text-muted">
                MIT licensed and open to inspection
              </small>
            </span>
            <span className="font-mono text-accent" aria-hidden="true">
              -&gt;
            </span>
          </a>
        </div>
        <Link className={textLink} to="/">
          back to vex{" "}
          <span className="ml-1.75 text-accent" aria-hidden="true">
            -&gt;
          </span>
        </Link>
      </main>
      <SiteFooter />
    </div>
  );
}

function SiteHeader() {
  return (
    <header
      className={`${frame} flex h-[72px] items-center justify-between border-b border-line max-narrow:h-16`}
    >
      <Link
        className="flex items-center gap-2.5 font-mono text-[14px] font-semibold tracking-[-0.04em] text-text no-underline"
        to="/"
        aria-label="Vex home"
      >
        <Mark />
        <span>vex</span>
      </Link>
      <nav
        className="flex items-center gap-7 font-mono text-[11px] text-muted max-narrow:gap-3 max-narrow:text-[10px]"
        aria-label="Main navigation"
      >
        <a
          className="no-underline transition-colors duration-150 hover:text-accent focus-visible:text-accent max-narrow:hidden"
          href="/#workspace"
        >
          workspace
        </a>
        <Link
          className="no-underline transition-colors duration-150 hover:text-accent focus-visible:text-accent"
          to="/download"
        >
          download
        </Link>
        <a
          className="no-underline transition-colors duration-150 hover:text-accent focus-visible:text-accent"
          href="https://github.com/Rayzerrek/Vex"
        >
          github
        </a>
      </nav>
    </header>
  );
}

function SiteFooter() {
  return (
    <footer
      className={`${frame} flex min-h-[72px] items-center justify-between border-t border-line font-mono text-[10px] lowercase tracking-[0.08em] text-faint max-narrow:min-h-[90px] max-narrow:flex-col max-narrow:items-start max-narrow:justify-center max-narrow:gap-2`}
    >
      <span>vex / native terminal workspace</span>
      <span>built for windows</span>
    </footer>
  );
}

function Mark() {
  return (
    <span
      className="relative inline-block size-[18px] skew-x-[-14deg]"
      aria-hidden="true"
    >
      <span className="absolute bottom-0 left-0.5 block h-3 w-[5px] bg-accent" />
      <span className="absolute right-0.5 bottom-0 block h-[18px] w-[5px] bg-accent" />
    </span>
  );
}

function Principle({ title, copy }: { title: string; copy: string }) {
  return (
    <article className="min-h-56.5 border-r border-line pt-7 pr-[26px] pb-[34px] pl-[26px] first:pl-0 last:border-r-0 max-narrow:min-h-auto max-narrow:border-b max-narrow:border-r-0 max-narrow:px-0 max-narrow:py-6 max-narrow:last:border-b-0">
      <h3 className="font-mono text-[13px] tracking-[-0.02em]">{title}</h3>
      <p className="mt-[30px] max-w-[260px] text-sm leading-[1.7] text-muted max-narrow:mt-4">
        {copy}
      </p>
    </article>
  );
}
