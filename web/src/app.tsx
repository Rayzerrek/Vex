import { useEffect, useRef, useState } from "react";
import { DocContent, docsPages } from "./docs";

const repository = "https://github.com/Rayzerrek/Vex";
const releases = `${repository}/releases`;

function Icon({
  name
}: {
  name: "sun" | "moon" | "download" | "search" | "close" | "menu" | "external";
}) {
  const paths = {
    sun: "M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8ZM12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5",
    moon: "M20 15.2A8.5 8.5 0 0 1 8.8 4 8.5 8.5 0 1 0 20 15.2Z",
    download: "M12 3v12m-5-5 5 5 5-5M5 16v5h14v-5",
    search: "M10.5 4a6.5 6.5 0 1 0 0 13 6.5 6.5 0 0 0 0-13ZM16 16l5 5",
    close: "m6 6 12 12M6 18 18 6",
    menu: "M4 6h16M4 12h16M4 18h16",
    external: "M8 5h11v11M19 5 5 19"
  };
  return (
    <svg
      width="20"
      height="20"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.6"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d={paths[name]} />
    </svg>
  );
}

function Mark() {
  return (
    <svg
      className="brand-mark"
      viewBox="0 0 48 48"
      fill="none"
      aria-hidden="true"
    >
      <rect width="48" height="48" rx="12" fill="currentColor" />
      <path
        d="M9 11h8l10 26h-8L9 11Zm23 0h8l-5 13h-8l5-13Zm-6 16h8l-4 10h-8l4-10Z"
        fill="var(--paper)"
      />
    </svg>
  );
}

function useTheme() {
  const [dark, setDark] = useState(false);
  useEffect(
    () => setDark(document.documentElement.classList.contains("dark")),
    []
  );
  function toggle() {
    const next = !dark;
    setDark(next);
    document.documentElement.classList.toggle("dark", next);
    document
      .querySelector('meta[name="theme-color"]')
      ?.setAttribute("content", next ? "#111214" : "#f6f6f5");
    try {
      localStorage.setItem("vex-theme", next ? "dark" : "light");
    } catch {
      /* The theme still works when browser storage is unavailable. */
    }
  }
  return { dark, toggle };
}

function SiteHeader({
  dark,
  toggle,
  isDocs
}: {
  dark: boolean;
  toggle: () => void;
  isDocs: boolean;
}) {
  return (
    <header className="site-header">
      <div className="header-inner">
        <a className="brand" href="/" aria-label="Vex home">
          <Mark />
          <span>vex</span>
        </a>
        <nav aria-label="Main navigation">
          <a href="/#features" className="nav-features">
            Features
          </a>
          <a
            href="/docs/getting-started"
            aria-current={isDocs ? "true" : undefined}
          >
            Docs
          </a>
          <a className="nav-github" href={repository}>
            GitHub <Icon name="external" />
          </a>
          <span className="nav-divider" />
          <button
            className="icon-button"
            type="button"
            onClick={toggle}
            aria-label={dark ? "Switch to light theme" : "Switch to dark theme"}
            aria-pressed={dark}
          >
            <Icon name={dark ? "sun" : "moon"} />
          </button>
          <a className="header-download" href="/#download">
            Download <Icon name="download" />
          </a>
        </nav>
      </div>
    </header>
  );
}

const workspaceFeatures = [
  {
    title: "Make room for your workflow.",
    description:
      "Split a tab into panes, rearrange them, and save the layout. Open it in another project with the directories and startup commands you chose.",
    link: "/docs/workspace#saved-layouts",
    linkText: "Explore workspaces",
    type: "panes"
  },
  {
    title: "Bring the right path to the prompt.",
    description:
      "Press Ctrl+Shift+F to find a file or folder at your cursor. Navigate directories and insert a path without leaving your shell or terminal application.",
    link: "/docs/shortcuts#path-picker",
    linkText: "Learn the path picker",
    type: "paths"
  },
  {
    title: "Know what needs your attention.",
    description:
      "See when a program is working, finished, failed, or waiting for input. Status stays visible across panes, tabs, and projects when programs report it.",
    link: "/docs/program-status",
    linkText: "Read about program status",
    type: "status"
  }
];

function WorkspaceDemo({
  selected,
  appearance
}: {
  selected: number;
  appearance: "light" | "dark";
}) {
  if (selected === 0 || selected === 1) {
    return (
      <img
        className="feature-screenshot"
        src={
          selected === 0
            ? `/shots/vex-${appearance}.png`
            : "/shots/vex-path-picker.png"
        }
        alt={
          selected === 0
            ? "Vex Development workspace with PowerShell on the left and a dev server and checks in two panes on the right"
            : "Vex path picker at the terminal cursor, listing files and directories with keyboard navigation hints"
        }
        width="1440"
        height="900"
        loading="lazy"
      />
    );
  }
  return (
    <div
      className="workspace-demo"
      aria-label="Program status illustration, not a live terminal"
    >
      <div className="demo-tabs">
        <span className="demo-project">
          vex <span>main</span>
        </span>
        <span>Program status</span>
      </div>
      <div className="status-demo">
        <div>
          <span className="status-symbol working">◌</span>
          <span>
            Build<small>Compiling the project</small>
          </span>
          <span>Working</span>
        </div>
        <div>
          <span className="status-symbol waiting">?</span>
          <span>
            Assistant<small>Approval requested</small>
          </span>
          <span>Needs input</span>
        </div>
        <div>
          <span className="status-symbol finished">✓</span>
          <span>
            Tests<small>Checks completed</small>
          </span>
          <span>Finished</span>
        </div>
        <p>Program-reported status, in one workspace.</p>
      </div>
      <div className="demo-bottom">
        <span>Workspace illustration</span>
        <span>Program status</span>
      </div>
    </div>
  );
}

function Home() {
  const [selected, setSelected] = useState(0);
  const [appearance, setAppearance] = useState<"light" | "dark">("dark");
  const feature = workspaceFeatures[selected];
  return (
    <main className="home-layout" id="main-content">
      <section className="hero page-width">
        <div className="hero-heading">
          <h1>
            Your terminals.
            <br />
            <span>One workspace.</span>
          </h1>
          <div className="hero-copy">
            <p>
              Your shells, projects, and split panes. Together in a quiet,
              native Windows app that leaves room for the work.
            </p>
            <div className="hero-actions">
              <a className="button button-primary" href="#download">
                <Icon name="download" />
                Get Vex for Windows
              </a>
              <a
                className="button button-secondary"
                href="/docs/getting-started"
              >
                Read the docs <Icon name="external" />
              </a>
            </div>
            <p className="hero-note">
              Windows 10 & 11 <span>·</span> Free & open source <span>·</span>{" "}
              No web wrapper
            </p>
          </div>
        </div>
        <figure className="product-figure">
          <div className="product-toolbar">
            <span>
              <span className="availability-dot" /> Vex / Development
            </span>
            <div
              className="appearance-switch"
              role="group"
              aria-label="Screenshot appearance"
            >
              {(["dark", "light"] as const).map((value) => (
                <button
                  type="button"
                  key={value}
                  aria-pressed={appearance === value}
                  onClick={() => setAppearance(value)}
                >
                  <Icon name={value === "dark" ? "moon" : "sun"} />
                  {value === "dark" ? "Dark" : "Light"}
                </button>
              ))}
            </div>
          </div>
          <div className="product-stage">
            <img
              src={`/shots/vex-${appearance}.png`}
              alt="Vex on Windows with three split terminal panes: PowerShell, a web dev server, and checks"
              width="1440"
              height="900"
              fetchPriority="high"
            />
          </div>
          <figcaption>
            <span>Three panes. One place to keep your train of thought.</span>
            <span>WPF / ConPTY / Ghostty VT</span>
          </figcaption>
        </figure>
      </section>
      <section className="features-section page-width" id="features">
        <p className="eyebrow section-label">The workspace</p>
        <div className="section-heading">
          <h2>
            Less window juggling.
            <br />
            More room to work.
          </h2>
          <p>
            Your shell is still your shell. Vex brings the workspace around it:
            projects, layouts, and the small things that keep you moving.
          </p>
        </div>
        <div
          className="feature-tabs"
          role="tablist"
          aria-label="Workspace features"
        >
          {["Projects & panes", "Path picker", "Program status"].map(
            (title, index) => (
              <button
                key={title}
                type="button"
                id={`feature-tab-${index}`}
                role="tab"
                aria-selected={selected === index}
                aria-controls="feature-panel"
                tabIndex={selected === index ? 0 : -1}
                onClick={() => setSelected(index)}
                onKeyDown={(event) => {
                  const next =
                    event.key === "ArrowRight"
                      ? (index + 1) % 3
                      : event.key === "ArrowLeft"
                        ? (index + 2) % 3
                        : event.key === "Home"
                          ? 0
                          : event.key === "End"
                            ? 2
                            : null;
                  if (next !== null) {
                    event.preventDefault();
                    setSelected(next);
                    document.getElementById(`feature-tab-${next}`)?.focus();
                  }
                }}
              >
                {title}
              </button>
            )
          )}
        </div>
        <div
          className="feature-panel"
          id="feature-panel"
          role="tabpanel"
          aria-labelledby={`feature-tab-${selected}`}
        >
          <div className="feature-visual">
            <WorkspaceDemo selected={selected} appearance={appearance} />
          </div>
          <div className="feature-copy">
            <span className="feature-index">
              0{selected + 1} /{" "}
              {selected === 0
                ? "Arrange"
                : selected === 1
                  ? "Navigate"
                  : "Stay in the loop"}
            </span>
            <h3>{feature.title}</h3>
            <p>{feature.description}</p>
            <a className="text-link" href={feature.link}>
              {feature.linkText}
            </a>
          </div>
        </div>
        <div className="native-details">
          <div>
            <h3>Windows, not a web wrapper.</h3>
            <p>
              Built with WPF and .NET 10. ConPTY runs your processes; Ghostty VT
              handles terminal emulation.
            </p>
          </div>
          <div>
            <h3>Use the shell you know.</h3>
            <p>
              PowerShell, Nushell, cmd, Git Bash, or WSL. Add your own shell
              profiles in Settings.
            </p>
          </div>
          <div>
            <h3>Keep your keys.</h3>
            <p>
              Vex uses Ctrl+Shift shortcuts. Your terminal applications keep
              their own Ctrl bindings.
            </p>
          </div>
        </div>
      </section>
      <section className="keyboard-section page-width">
        <div className="keyboard-copy">
          <p className="eyebrow section-label">At your fingertips</p>
          <h2>
            A workspace at
            <br />
            your fingertips.
          </h2>
          <p>
            Split a pane, switch a project, or find a command. The palette keeps
            workspace actions close, while your shell keeps its own keys.
          </p>
          <div className="shortcut-hint">
            <kbd>Ctrl</kbd>
            <kbd>Shift</kbd>
            <kbd>P</kbd>
            <span>One shortcut. All your actions.</span>
          </div>
          <a className="text-link" href="/docs/shortcuts">
            Find your next shortcut <Icon name="external" />
          </a>
        </div>
        <figure className="palette-figure">
          <img
            src="/shots/vex-palette.png"
            alt="Vex command palette showing new tab, saved layout, tab overview, and split pane actions"
            width="1440"
            height="900"
            loading="lazy"
          />
          <figcaption>The command palette, inside the native app.</figcaption>
        </figure>
      </section>
      <section className="docs-invitation page-width">
        <div>
          <p className="eyebrow section-label">Documentation</p>
          <h2>
            A short path from
            <br />
            installing to working.
          </h2>
          <p>
            Small, straightforward guides. Get set up, learn the workspace, and
            make yourself at home.
          </p>
          <a className="text-link" href="/docs/getting-started">
            Open the documentation <Icon name="external" />
          </a>
        </div>
        <nav className="guide-links" aria-label="Explore the guides">
          {[
            {
              slug: "getting-started",
              title: "Installation",
              detail: "From download to your first prompt"
            },
            {
              slug: "workspace",
              title: "Your workspace",
              detail: "Projects, panes, and saved layouts"
            },
            {
              slug: "appearance",
              title: "Make it feel like you",
              detail: "Themes, fonts, and the little details"
            }
          ].map((guide, index) => (
            <a key={guide.slug} href={`/docs/${guide.slug}`}>
              <span className="guide-number">0{index + 1}</span>
              <span>
                <strong>{guide.title}</strong>
                <small>{guide.detail}</small>
              </span>
              <Icon name="external" />
            </a>
          ))}
        </nav>
      </section>
      <section className="download-section page-width" id="download">
        <div>
          <p className="eyebrow section-label">Ready when you are</p>
          <h2>
            Make yourself
            <br />
            at home.
          </h2>
          <p>
            Vex is free and open source.
            <br />
            Available for Windows 10 (1809+) and Windows 11.
          </p>
          <a className="text-link" href={`${repository}/blob/main/LICENSE`}>
            GPL-3.0 license
          </a>
        </div>
        <div className="download-options">
          <a className="download-option" href={releases}>
            <span className="download-icon">
              <Icon name="download" />
            </span>
            <span>
              <strong>Windows installer</strong>
              <small>Install Vex with the latest .msi release.</small>
            </span>
            <Icon name="external" />
          </a>
          <a className="download-option" href={releases}>
            <span className="download-icon">
              <Icon name="download" />
            </span>
            <span>
              <strong>Portable version</strong>
              <small>Unpack the .zip and run. No .NET install needed.</small>
            </span>
            <Icon name="external" />
          </a>
          <a className="source-link" href={repository}>
            Prefer to build it yourself? Browse the source on GitHub.
          </a>
        </div>
      </section>
    </main>
  );
}

function Docs({ slug }: { slug: string }) {
  const pageIndex = docsPages.findIndex((page) => page.slug === slug);
  const page = docsPages[pageIndex];
  const [query, setQuery] = useState("");
  const [menuOpen, setMenuOpen] = useState(false);
  const searchRef = useRef<HTMLInputElement>(null);
  useEffect(() => {
    setMenuOpen(false);
    setQuery("");
  }, [slug]);
  useEffect(() => {
    function handleKey(event: KeyboardEvent) {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
        event.preventDefault();
        setMenuOpen(true);
        requestAnimationFrame(() => searchRef.current?.focus());
      }
      if (event.key === "Escape") {
        setQuery("");
        setMenuOpen(false);
      }
    }
    window.addEventListener("keydown", handleKey);
    return () => window.removeEventListener("keydown", handleKey);
  }, []);
  if (!page) return <NotFound />;
  const filtered = docsPages.filter((item) =>
    `${item.title} ${item.description}`
      .toLowerCase()
      .includes(query.toLowerCase().trim())
  );
  return (
    <main className="docs-layout page-width" id="main-content">
      <div className="docs-mobile-bar">
        <span>Documentation</span>
        <button
          className="icon-button"
          type="button"
          aria-expanded={menuOpen}
          aria-controls="docs-sidebar"
          aria-label={
            menuOpen
              ? "Close documentation navigation"
              : "Open documentation navigation"
          }
          onClick={() => setMenuOpen(!menuOpen)}
        >
          <Icon name={menuOpen ? "close" : "menu"} />
        </button>
      </div>
      <aside
        className={`docs-sidebar ${menuOpen ? "is-open" : ""}`}
        id="docs-sidebar"
      >
        <label className="docs-search">
          <Icon name="search" />
          <input
            ref={searchRef}
            type="search"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="Find a guide…"
            aria-label="Search documentation guides"
          />
          <kbd>Ctrl K</kbd>
        </label>
        <nav aria-label="Documentation">
          {Array.from(new Set(docsPages.map((item) => item.group))).map(
            (group) => {
              const items = filtered.filter((item) => item.group === group);
              return items.length > 0 ? (
                <div className="docs-nav-group" key={group}>
                  <h2>{group}</h2>
                  {items.map((item) => (
                    <a
                      key={item.slug}
                      href={`/docs/${item.slug}`}
                      aria-current={item.slug === slug ? "page" : undefined}
                    >
                      {item.title}
                    </a>
                  ))}
                </div>
              ) : null;
            }
          )}
          {filtered.length === 0 && (
            <p className="search-empty" role="status">
              No matching guides. Try “shell”, “theme”, or “panes”.
            </p>
          )}
        </nav>
        <a className="docs-source" href={`${repository}/tree/main/docs`}>
          Documentation source <Icon name="external" />
        </a>
      </aside>
      <article className="doc-article">
        <div className="doc-breadcrumb">
          <a href="/docs/getting-started">Docs</a>
          <span>/</span>
          <span>{page.group}</span>
        </div>
        <h1>{page.title}</h1>
        <p className="doc-description">{page.description}</p>
        <div className="mobile-toc">
          <details>
            <summary>On this page</summary>
            {page.sections.map((section) => (
              <a key={section.id} href={`#${section.id}`}>
                {section.title}
              </a>
            ))}
          </details>
        </div>
        <DocContent slug={slug} />
        <nav className="doc-pagination" aria-label="Documentation pages">
          {docsPages[pageIndex - 1] ? (
            <a href={`/docs/${docsPages[pageIndex - 1].slug}`}>
              <small>Previous</small>
              {docsPages[pageIndex - 1].title}
            </a>
          ) : (
            <span />
          )}
          {docsPages[pageIndex + 1] && (
            <a href={`/docs/${docsPages[pageIndex + 1].slug}`}>
              <small>Next</small>
              {docsPages[pageIndex + 1].title}
            </a>
          )}
        </nav>
      </article>
      <aside className="docs-toc">
        <h2>On this page</h2>
        <nav aria-label="On this page">
          {page.sections.map((section) => (
            <a key={section.id} href={`#${section.id}`}>
              {section.title}
            </a>
          ))}
        </nav>
        <a className="toc-help" href={`${repository}/issues`}>
          Something missing?
          <br />
          <span>Let us know on GitHub.</span>
        </a>
      </aside>
    </main>
  );
}

function NotFound() {
  return (
    <main className="not-found page-width" id="main-content">
      <h1>That page isn’t here.</h1>
      <p>
        Head back to the workspace, or find what you need in the documentation.
      </p>
      <a className="button button-primary" href="/docs/getting-started">
        Open the docs
      </a>
      <a className="text-link" href="/">
        Back to home
      </a>
    </main>
  );
}

export function App({ initialPath }: { initialPath?: string }) {
  const [path, setPath] = useState(
    initialPath ??
      (typeof window === "undefined" ? "/" : window.location.pathname)
  );
  const { dark, toggle } = useTheme();
  const normalized = path.replace(/\/+$/, "") || "/";
  const isDocs = normalized === "/docs" || normalized.startsWith("/docs/");
  const slug =
    normalized === "/docs" ? "getting-started" : normalized.split("/")[2];
  useEffect(() => {
    const sync = () => setPath(window.location.pathname);
    window.addEventListener("popstate", sync);
    function navigate(event: MouseEvent) {
      if (
        event.defaultPrevented ||
        event.button !== 0 ||
        event.ctrlKey ||
        event.metaKey ||
        event.shiftKey ||
        event.altKey
      )
        return;
      if (!(event.target instanceof Element)) return;
      const anchor = event.target.closest("a");
      if (!anchor || anchor.target || anchor.hasAttribute("download")) return;
      const url = new URL(anchor.href);
      if (
        url.origin !== window.location.origin ||
        url.pathname === window.location.pathname
      )
        return;
      event.preventDefault();
      window.history.pushState(null, "", url);
      setPath(url.pathname);
      if (url.hash)
        requestAnimationFrame(() =>
          document
            .getElementById(decodeURIComponent(url.hash.slice(1)))
            ?.scrollIntoView()
        );
      else window.scrollTo(0, 0);
      requestAnimationFrame(() => {
        const main = document.getElementById("main-content");
        main?.setAttribute("tabindex", "-1");
        main?.focus({ preventScroll: true });
      });
    }
    document.addEventListener("click", navigate);
    return () => {
      window.removeEventListener("popstate", sync);
      document.removeEventListener("click", navigate);
    };
  }, []);
  useEffect(() => {
    const current = docsPages.find((item) => item.slug === slug);
    document.title = isDocs
      ? `${current?.title ?? "Page not found"} — Vex Docs`
      : normalized === "/"
        ? "Vex — Your terminals. One workspace."
        : "Page not found — Vex";
    document
      .querySelector('meta[name="description"]')
      ?.setAttribute(
        "content",
        isDocs && current
          ? current.description
          : "A native terminal workspace for Windows. Keep your projects, shells, and split panes together."
      );
  }, [normalized, isDocs, slug]);
  return (
    <div
      className={normalized === "/" ? "site-shell home-shell" : "site-shell"}
    >
      <a className="skip-link" href="#main-content">
        Skip to content
      </a>
      <SiteHeader dark={dark} toggle={toggle} isDocs={isDocs} />
      {isDocs ? (
        <Docs slug={slug} />
      ) : normalized === "/" ? (
        <Home />
      ) : (
        <NotFound />
      )}
      <footer className="site-footer page-width">
        <a className="brand footer-brand" href="/">
          <Mark />
          <span>vex</span>
        </a>
        <span>A little more space to work.</span>
        <nav aria-label="Footer">
          <a href="/docs/getting-started">Documentation</a>
          <a href={repository}>GitHub</a>
          <span>© 2026 Vex</span>
        </nav>
      </footer>
    </div>
  );
}
