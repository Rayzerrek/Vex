import { useEffect, useState, type ReactNode } from "react";
import { Icon, Mark } from "./icons";
import { StateIcon } from "./state-icon";
import { useInputModality } from "./use-input-modality";

const repository = "https://github.com/Rayzerrek/Vex";

export function SiteLayout({
  isDocs,
  children
}: {
  isDocs: boolean;
  children: ReactNode;
}) {
  useInputModality();
  const [dark, setDark] = useState(false);
  useEffect(
    () => setDark(document.documentElement.classList.contains("dark")),
    []
  );
  function toggleTheme() {
    const next = !dark;
    setDark(next);
    document.documentElement.classList.toggle("dark", next);
    document
      .querySelector('meta[name="theme-color"]')
      ?.setAttribute("content", next ? "#101117" : "#f8f9fc");
    try {
      localStorage.setItem("vex-theme", next ? "dark" : "light");
    } catch {
      /* Storage is optional; theme switching is not. */
    }
  }
  return (
    <div className={`site-shell ${isDocs ? "docs-shell" : "home-shell"}`}>
      <a className="skip-link" href="#main-content">
        Skip to content
      </a>
      <header className="site-header">
        <div className="header-inner">
          <a className="brand" href="/" aria-label="Vex home">
            <Mark />
            <span>vex</span>
          </a>
          <nav aria-label="Main navigation">
            <a className="nav-features" href="/#features">
              Why Vex
            </a>
            <a
              href="/docs/getting-started"
              aria-current={isDocs ? "true" : undefined}
            >
              Docs
            </a>
            <a className="nav-github" href={repository}>
              GitHub
              <Icon name="external" />
            </a>
            <button
              className="icon-button theme-toggle"
              type="button"
              onClick={toggleTheme}
              aria-label={
                dark ? "Switch to light theme" : "Switch to dark theme"
              }
              aria-pressed={dark}
            >
              <StateIcon active={dark} from="moon" to="sun" />
            </button>
            <a className="header-download" href="/#download">
              Download
              <Icon name="download" />
            </a>
          </nav>
        </div>
      </header>
      {children}
      <footer className="site-footer page-width">
        <a className="brand footer-brand" href="/" aria-label="Vex home">
          <Mark />
          <span>vex</span>
        </a>
        <span>A little more space to work.</span>
        <nav aria-label="Footer">
          <a href="/docs/getting-started">Documentation</a>
          <a href={repository}>Source code</a>
          <span>© 2026 Vex</span>
        </nav>
      </footer>
    </div>
  );
}
