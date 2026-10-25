import { Link } from "@tanstack/react-router";
import { guidePath } from "../../routes";
import { docsPages } from "./content";

export function DocsPagination({ slug }: { slug: string }) {
  const index = docsPages.findIndex((page) => page.slug === slug);
  const previous = docsPages[index - 1];
  const next = docsPages[index + 1];
  return (
    <nav className="doc-pagination" aria-label="Documentation pages">
      {previous ? (
        <Link to={guidePath(previous.slug)}>
          <small>Previous</small>
          {previous.title}
        </Link>
      ) : (
        <span />
      )}
      {next && (
        <Link to={guidePath(next.slug)}>
          <small>Next</small>
          {next.title}
        </Link>
      )}
    </nav>
  );
}
