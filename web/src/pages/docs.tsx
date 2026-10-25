import { useEffect } from "react";
import { Link, useLocation } from "@tanstack/react-router";
import { docsPath, resolveRoute } from "../routes";
import { DocContent, docsPages } from "../features/docs/content";
import { DocsSidebar } from "../features/docs/sidebar";
import { DocsPagination } from "../features/docs/pagination";
import { TableOfContents } from "../features/docs/table-of-contents";
import { useActiveSection } from "../features/docs/use-active-section";
import { NotFound } from "./not-found";

export function DocsRoute() {
  const path = useLocation({ select: (location) => location.pathname });
  const route = resolveRoute(path);
  return route.kind === "docs" ? (
    <Docs key={route.slug} slug={route.slug} />
  ) : (
    <NotFound />
  );
}

function Docs({ slug }: { slug: string }) {
  const page = docsPages.find((page) => page.slug === slug);
  const active = useActiveSection();
  useEffect(() => {
    document.title = `${page?.title ?? "Page not found"} — Vex Docs`;
    document
      .querySelector('meta[name="description"]')
      ?.setAttribute("content", page?.description ?? "Find Vex documentation.");
  }, [page]);
  if (!page) return <NotFound />;
  return (
    <main className="docs-layout page-width" id="main-content" tabIndex={-1}>
      <DocsSidebar slug={slug} />
      <article className="doc-article page-enter">
        <div className="doc-breadcrumb">
          <Link to={docsPath}>Docs</Link>
          <span>/</span>
          <span>{page.group}</span>
        </div>
        <h1>{page.title}</h1>
        <p className="doc-description">{page.description}</p>
        <TableOfContents sections={page.sections} active={active} mobile />
        <DocContent slug={slug} />
        <DocsPagination slug={slug} />
      </article>
      <TableOfContents sections={page.sections} active={active} />
    </main>
  );
}
