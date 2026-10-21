import { useEffect, useRef, useState } from "react";
import { Icon } from "../components/icons";
import { StateIcon } from "../components/state-icon";
import { DocContent, docsPages } from "../features/docs/content";
import { useActiveSection } from "../features/docs/use-active-section";
import { NotFound } from "./not-found";

export function Docs({ slug }: { slug: string }) {
  const pageIndex = docsPages.findIndex((page) => page.slug === slug);
  const page = docsPages[pageIndex];
  const [query, setQuery] = useState("");
  const [menuOpen, setMenuOpen] = useState(false);
  const searchRef = useRef<HTMLInputElement>(null);
  const menuRef = useRef<HTMLButtonElement>(null);
  const active = useActiveSection();
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
        if (
          window.matchMedia("(max-width: 760px)").matches &&
          document.activeElement === searchRef.current
        )
          menuRef.current?.focus();
      }
    }
    window.addEventListener("keydown", handleKey);
    return () => window.removeEventListener("keydown", handleKey);
  }, []);
  if (!page) return <NotFound />;
  const normalizedQuery = query.toLowerCase().trim();
  const filtered = docsPages.filter((item) =>
    `${item.title} ${item.description} ${item.sections.map((section) => section.title).join(" ")}`
      .toLowerCase()
      .includes(normalizedQuery)
  );
  const previous = docsPages[pageIndex - 1];
  const next = docsPages[pageIndex + 1];
  const toc = page.sections.map((section) => (
    <a
      key={section.id}
      href={`#${section.id}`}
      aria-current={active === section.id ? "location" : undefined}
    >
      {section.title}
    </a>
  ));
  return (
    <main className="docs-layout page-width" id="main-content">
      <div className="docs-mobile-bar">
        <span>Documentation</span>
        <button
          ref={menuRef}
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
          <StateIcon active={menuOpen} from="menu" to="close" />
        </button>
      </div>
      <aside
        className={`docs-sidebar ${menuOpen ? "is-open" : ""}`}
        id="docs-sidebar"
      >
        <a className="docs-home" href="/docs/getting-started">
          The Vex guide
        </a>
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
              return items.length ? (
                <div className="docs-nav-group" key={group}>
                  <h2>{group}</h2>
                  {items.map((item) => (
                    <div key={item.slug}>
                      <a
                        href={`/docs/${item.slug}`}
                        aria-current={item.slug === slug ? "page" : undefined}
                      >
                        {item.title}
                      </a>
                      {normalizedQuery &&
                        item.sections
                          .filter((section) =>
                            section.title
                              .toLowerCase()
                              .includes(normalizedQuery)
                          )
                          .map((section) => (
                            <a
                              className="search-section"
                              key={section.id}
                              href={`/docs/${item.slug}#${section.id}`}
                              onClick={() => {
                                setMenuOpen(false);
                                setQuery("");
                              }}
                            >
                              {section.title}
                            </a>
                          ))}
                    </div>
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
        <a
          className="docs-source"
          href="https://github.com/Rayzerrek/Vex/tree/main/web/src/features/docs"
        >
          Edit these docs
          <Icon name="external" />
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
            {toc}
          </details>
        </div>
        <DocContent slug={slug} />
        <nav className="doc-pagination" aria-label="Documentation pages">
          {previous ? (
            <a href={`/docs/${previous.slug}`}>
              <small>Previous</small>
              {previous.title}
            </a>
          ) : (
            <span />
          )}
          {next && (
            <a href={`/docs/${next.slug}`}>
              <small>Next</small>
              {next.title}
            </a>
          )}
        </nav>
      </article>
      <aside className="docs-toc">
        <h2>On this page</h2>
        <nav aria-label="On this page">{toc}</nav>
        <a className="toc-help" href="https://github.com/Rayzerrek/Vex/issues">
          Something missing?<span>Let us know on GitHub.</span>
          <Icon name="external" />
        </a>
      </aside>
    </main>
  );
}
