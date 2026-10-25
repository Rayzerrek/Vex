import { Link } from "@tanstack/react-router";
import { docsPath, repository } from "../routes";

export function SiteFooter() {
  return (
    <footer className="page-width flex flex-wrap items-center gap-4 border-t border-rule py-7 text-xs text-soft">
      <span>© 2026 Vex</span>
      <nav
        aria-label="Footer"
        className="ml-auto flex gap-5 [&_a:hover]:text-ink"
      >
        <Link to={docsPath}>Documentation</Link>
        <a href={repository}>Source code</a>
      </nav>
    </footer>
  );
}
