import { Link } from "@tanstack/react-router";
import { TerminalDemo } from "./terminal-demo.tsx";
import "./app.css";

export function HomePage() {
  return (
    <div className="site-shell">
      <SiteHeader />
      <main>
        <section className="hero section-frame" id="top">
          <div className="hero-copy">
            <p className="eyebrow">native terminal workspace</p>
            <h1>Your terminal. In its place.</h1>
            <p className="hero-lede">
              A focused Windows workspace for terminals, files, and code.
            </p>
            <div className="hero-actions">
              <Link className="button button-primary" to="/download">
                download
              </Link>
              <a className="text-link" href="https://github.com/Rayzerrek/Vex">
                view source <span aria-hidden="true">-&gt;</span>
              </a>
            </div>
          </div>
          <TerminalDemo />
        </section>

        <section className="quiet-intro section-frame" id="workspace">
          <p className="eyebrow">the workspace</p>
          <h2>Everything you need. Nothing in the way.</h2>
          <p>
            Vex keeps the useful parts of a terminal workspace close and the
            interface quiet.
          </p>
        </section>

        <section className="principles section-frame" aria-label="Principles">
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

        <section className="architecture section-frame">
          <div>
            <p className="eyebrow">under the surface</p>
            <h2>Small pieces. Clear boundaries.</h2>
            <p>
              Vex is built around the work a terminal needs to do well: start
              quickly, render reliably, and stay out of the way.
            </p>
          </div>
          <div className="architecture-detail">
            <pre aria-label="Vex architecture">
              <code>{`windows/Vex.App
  shell             WPF window and workspace state
  terminal          ConPTY sessions and input
  editor            syntax-aware file editing
  explorer          project files and folders

Vendor/XtermSharp   terminal emulation`}</code>
            </pre>
            <dl className="spec-list">
              <div>
                <dt>platform</dt>
                <dd>Windows 10 1809+</dd>
              </div>
              <div>
                <dt>runtime</dt>
                <dd>.NET 8</dd>
              </div>
              <div>
                <dt>license</dt>
                <dd>MIT</dd>
              </div>
            </dl>
          </div>
        </section>

        <section className="download-callout section-frame" id="download">
          <div>
            <p className="eyebrow">get started</p>
            <h2>Open a better default.</h2>
          </div>
          <div className="download-copy">
            <p>
              Download the latest Windows installer or build Vex from source.
            </p>
            <Link className="text-link" to="/download">
              choose a download <span aria-hidden="true">-&gt;</span>
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
    <div className="site-shell download-page">
      <SiteHeader />
      <main className="download-main section-frame">
        <p className="eyebrow">download</p>
        <h1>Start with Vex.</h1>
        <p className="download-lede">
          A native terminal workspace for Windows developers.
        </p>
        <div className="download-options">
          <a
            className="download-option"
            href="https://github.com/Rayzerrek/Vex/releases"
            target="_blank"
            rel="noreferrer"
          >
            <span>
              <strong>windows installer</strong>
              <small>latest release from GitHub</small>
            </span>
            <span aria-hidden="true">-&gt;</span>
          </a>
          <a
            className="download-option"
            href="https://github.com/Rayzerrek/Vex"
            target="_blank"
            rel="noreferrer"
          >
            <span>
              <strong>build from source</strong>
              <small>MIT licensed and open to inspection</small>
            </span>
            <span aria-hidden="true">-&gt;</span>
          </a>
        </div>
        <Link className="text-link" to="/">
          back to vex <span aria-hidden="true">-&gt;</span>
        </Link>
      </main>
      <SiteFooter />
    </div>
  );
}

function SiteHeader() {
  return (
    <header className="site-header section-frame">
      <Link className="wordmark" to="/" aria-label="Vex home">
        <Mark />
        <span>vex</span>
      </Link>
      <nav aria-label="Main navigation">
        <a href="/#workspace">workspace</a>
        <Link to="/download">download</Link>
        <a href="https://github.com/Rayzerrek/Vex">github</a>
      </nav>
    </header>
  );
}

function SiteFooter() {
  return (
    <footer className="site-footer section-frame">
      <span>vex / native terminal workspace</span>
      <span>built for windows</span>
    </footer>
  );
}

function Mark() {
  return (
    <span className="mark" aria-hidden="true">
      <span />
      <span />
    </span>
  );
}

function Principle({ title, copy }: { title: string; copy: string }) {
  return (
    <article className="principle">
      <h3>{title}</h3>
      <p>{copy}</p>
    </article>
  );
}
