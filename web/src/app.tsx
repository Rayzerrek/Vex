import { useEffect, useState } from "react";
import { SiteLayout } from "./components/site-layout";
import { docsPages } from "./features/docs/content";
import { Docs } from "./pages/docs";
import { Home } from "./pages/home";
import { NotFound } from "./pages/not-found";

export function App({ initialPath }: { initialPath?: string }) {
  const [path, setPath] = useState(
    initialPath ??
      (typeof window === "undefined" ? "/" : window.location.pathname)
  );
  const normalized = path.replace(/\/+$/, "") || "/";
  const isDocs = normalized === "/docs" || normalized.startsWith("/docs/");
  const slug =
    normalized === "/docs"
      ? "getting-started"
      : normalized.slice("/docs/".length);
  useEffect(() => {
    function scrollToHash() {
      if (!window.location.hash) {
        window.scrollTo(0, 0);
        return;
      }
      try {
        document
          .getElementById(decodeURIComponent(window.location.hash.slice(1)))
          ?.scrollIntoView();
      } catch {
        /* Malformed fragment identifiers should not break navigation. */
      }
    }
    function sync() {
      setPath(window.location.pathname);
      requestAnimationFrame(scrollToHash);
    }
    function navigate(event: MouseEvent) {
      if (
        event.defaultPrevented ||
        event.button !== 0 ||
        event.ctrlKey ||
        event.metaKey ||
        event.shiftKey ||
        event.altKey ||
        !(event.target instanceof Element)
      )
        return;
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
      requestAnimationFrame(() => {
        scrollToHash();
        const main = document.getElementById("main-content");
        main?.setAttribute("tabindex", "-1");
        main?.focus({ preventScroll: true });
      });
    }
    window.addEventListener("popstate", sync);
    document.addEventListener("click", navigate);
    return () => {
      window.removeEventListener("popstate", sync);
      document.removeEventListener("click", navigate);
    };
  }, []);
  useEffect(() => {
    const page = docsPages.find((item) => item.slug === slug);
    document.title = isDocs
      ? `${page?.title ?? "Page not found"} — Vex Docs`
      : normalized === "/"
        ? "Vex — Room for the real work."
        : "Page not found — Vex";
    document
      .querySelector('meta[name="description"]')
      ?.setAttribute(
        "content",
        isDocs && page
          ? page.description
          : "A native terminal workspace for Windows. Your shells, tabs, and split panes, without the extra layers."
      );
  }, [normalized, isDocs, slug]);
  return (
    <SiteLayout isDocs={isDocs}>
      {isDocs ? (
        <Docs key={slug} slug={slug} />
      ) : normalized === "/" ? (
        <Home />
      ) : (
        <NotFound />
      )}
    </SiteLayout>
  );
}
