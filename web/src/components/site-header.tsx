import { Link } from "@tanstack/react-router";
import { docsPath, homePath, repository } from "../routes";
import { Brand } from "./brand";
import { ThemeToggle } from "./theme-toggle";

export function SiteHeader() {
  return (
    <header className="site-header sticky top-0 z-20 border-b border-rule bg-paper">
      <div className="header-inner flex h-16 items-center justify-between gap-4">
        <Brand />
        <nav
          aria-label="Main navigation"
          className="flex items-center gap-4 text-nav text-soft sm:gap-6 [&_a:hover]:text-ink"
        >
          <Link
            className="hidden sm:block"
            to={homePath}
            hash="features"
            activeOptions={{ exact: true }}
          >
            Why Vex
          </Link>
          <Link to={docsPath}>Docs</Link>
          <a className="hidden sm:block" href={repository}>
            GitHub
          </a>
          <ThemeToggle />
          <Link to={homePath} hash="download">
            Download
          </Link>
        </nav>
      </div>
    </header>
  );
}
