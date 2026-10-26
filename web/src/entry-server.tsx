import { Suspense } from "react";
import { renderToString } from "react-dom/server";
import { createMemoryHistory, RouterProvider } from "@tanstack/react-router";
import { createSiteRouter } from "./router";
import { docsPages } from "./features/docs/content";
import { guidePath, homePath, resolveRoute } from "./routes";
export { legacyPaths, resolveRoute } from "./routes";

export const staticPages = [
  {
    path: homePath,
    title: "Vex — Room for the real work.",
    description:
      "A native terminal workspace for Windows. Your shells, tabs, and split panes, without the extra layers."
  },
  ...docsPages.map((page) => ({
    path: guidePath(page.slug),
    title: `${page.title} — Vex Docs`,
    description: page.description
  })),
  {
    path: "/404",
    title: "Page not found — Vex",
    description: "Find Vex documentation or return to the homepage."
  }
];

export async function render(path: string) {
  const router = createSiteRouter(
    createMemoryHistory({ initialEntries: [resolveRoute(path).path] })
  );
  await router.load();
  // TanStack adds this outer boundary in the browser; mirror it in static HTML.
  return renderToString(
    <Suspense>
      <RouterProvider router={router} />
    </Suspense>
  );
}
