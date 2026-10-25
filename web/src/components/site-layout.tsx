import { useEffect, useRef } from "react";
import { Outlet, useLocation } from "@tanstack/react-router";
import { resolveRoute } from "../routes";
import { SiteHeader } from "./site-header";
import { SiteFooter } from "./site-footer";
import { useInputModality } from "./use-input-modality";

export function RouteLayout() {
  useInputModality();
  const path = useLocation({ select: (location) => location.pathname });
  const route = resolveRoute(path);
  const previousPath = useRef(path);
  useEffect(() => {
    if (previousPath.current !== path) {
      document.getElementById("main-content")?.focus({ preventScroll: true });
      previousPath.current = path;
    }
    if (route.kind === "docs") return;
    document.title =
      route.kind === "home"
        ? "Vex — Room for the real work."
        : "Page not found — Vex";
    document
      .querySelector('meta[name="description"]')
      ?.setAttribute(
        "content",
        "A native terminal workspace for Windows. Your shells, tabs, and split panes, without the extra layers."
      );
  }, [path, route.kind]);
  return (
    <div className={route.kind === "docs" ? "docs-shell" : "home-shell"}>
      <a
        className="fixed top-2 left-2 z-50 -translate-y-20 rounded-md bg-ink px-4 py-3 text-paper focus:translate-y-0"
        href="#main-content"
      >
        Skip to content
      </a>
      <SiteHeader />
      <Outlet />
      <SiteFooter />
    </div>
  );
}
