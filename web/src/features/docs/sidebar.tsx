import { useEffect, useRef, useState } from "react";
import { Link } from "@tanstack/react-router";
import { MagnifyingGlassIcon } from "@phosphor-icons/react/dist/csr/MagnifyingGlass";
import { ListIcon } from "@phosphor-icons/react/dist/csr/List";
import { XIcon } from "@phosphor-icons/react/dist/csr/X";
import { ArrowUpRightIcon } from "@phosphor-icons/react/dist/csr/ArrowUpRight";
import { docsPath, guidePath, repository } from "../../routes";
import { docsPages } from "./content";

const groups = [...new Set(docsPages.map((page) => page.group))];
const searchIndex = docsPages.map((page) => ({
  page,
  text: `${page.title} ${page.description} ${page.sections.map((section) => section.title).join(" ")}`.toLowerCase()
}));

export function DocsSidebar({ slug }: { slug: string }) {
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const searchRef = useRef<HTMLInputElement>(null);
  const menuRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    function handleKey(event: KeyboardEvent) {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
        event.preventDefault();
        setOpen(true);
        requestAnimationFrame(() => searchRef.current?.focus());
      }
      if (event.key === "Escape") {
        setQuery("");
        setOpen(false);
        if (
          window.matchMedia("(width < 48rem)").matches &&
          document.activeElement === searchRef.current
        )
          menuRef.current?.focus();
      }
    }
    window.addEventListener("keydown", handleKey);
    return () => window.removeEventListener("keydown", handleKey);
  }, []);
  const normalized = query.toLowerCase().trim();
  const filtered = searchIndex
    .filter((item) => item.text.includes(normalized))
    .map((item) => item.page);
  function close() {
    setOpen(false);
    setQuery("");
  }
  return (
    <>
      <div className="docs-mobile-bar">
        <span>Documentation</span>
        <button
          ref={menuRef}
          className="icon-button"
          type="button"
          aria-expanded={open}
          aria-controls="docs-sidebar"
          aria-label={
            open
              ? "Close documentation navigation"
              : "Open documentation navigation"
          }
          onClick={() => setOpen(!open)}
        >
          {open ? (
            <XIcon aria-hidden="true" />
          ) : (
            <ListIcon aria-hidden="true" />
          )}
        </button>
      </div>
      <aside
        className={`docs-sidebar ${open ? "is-open" : ""}`}
        id="docs-sidebar"
      >
        <Link className="docs-home" to={docsPath}>
          The Vex guide
        </Link>
        <label className="docs-search">
          <MagnifyingGlassIcon aria-hidden="true" />
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
          {groups.map((group) => {
            const items = filtered.filter((item) => item.group === group);
            return items.length ? (
              <div className="docs-nav-group" key={group}>
                <h2>{group}</h2>
                {items.map((item) => (
                  <div key={item.slug}>
                    <Link
                      to={guidePath(item.slug)}
                      aria-current={item.slug === slug ? "page" : undefined}
                      onClick={close}
                    >
                      {item.title}
                    </Link>
                    {normalized &&
                      item.sections
                        .filter((section) =>
                          section.title.toLowerCase().includes(normalized)
                        )
                        .map((section) => (
                          <Link
                            className="search-section"
                            key={section.id}
                            to={guidePath(item.slug)}
                            hash={section.id}
                            onClick={close}
                          >
                            {section.title}
                          </Link>
                        ))}
                  </div>
                ))}
              </div>
            ) : null;
          })}
          {!filtered.length && (
            <p className="search-empty" role="status">
              No matching guides. Try “shell”, “theme”, or “panes”.
            </p>
          )}
        </nav>
        <a
          className="docs-source"
          href={`${repository}/tree/main/web/src/features/docs`}
        >
          Edit these docs <ArrowUpRightIcon aria-hidden="true" />
        </a>
      </aside>
    </>
  );
}
