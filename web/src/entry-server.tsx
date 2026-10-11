import { renderToString } from "react-dom/server";
import { App } from "./app";
import { docsPages } from "./docs";

export const staticPages = [
  {
    path: "/",
    title: "Vex — Your terminals. One workspace.",
    description:
      "A native terminal workspace for Windows. Keep your projects, shells, and split panes together."
  },
  {
    path: "/docs",
    title: "Installation — Vex Docs",
    description: docsPages[0].description
  },
  ...docsPages.map((page) => ({
    path: `/docs/${page.slug}`,
    title: `${page.title} — Vex Docs`,
    description: page.description
  })),
  {
    path: "/404",
    title: "Page not found — Vex",
    description: "Find Vex documentation or return to the homepage."
  }
];

export function render(path: string) {
  return renderToString(<App initialPath={path} />);
}
