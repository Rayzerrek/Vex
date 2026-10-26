import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { DocContent, docsPages } from "./features/docs/content";
import { render, staticPages } from "./entry-server";
import {
  docsPath,
  guidePath,
  homePath,
  legacyPaths,
  resolveRoute
} from "./routes";

const guide = (slug: string) => render(guidePath(slug));

describe("documentation", () => {
  it("has unique routes and matching section anchors", () => {
    expect(new Set(docsPages.map((page) => guidePath(page.slug))).size).toBe(
      docsPages.length
    );
    for (const page of docsPages) {
      const html = renderToStaticMarkup(<DocContent slug={page.slug} />);
      expect(new Set(page.sections.map((section) => section.id)).size).toBe(
        page.sections.length
      );
      for (const section of page.sections) {
        expect(html).toContain(`id="${section.id}"`);
        expect(html).toContain(`id="${section.id}-heading"`);
      }
    }
  });

  it("only links to existing guides and sections", async () => {
    for (const path of [
      homePath,
      docsPath,
      ...docsPages.map((page) => guidePath(page.slug))
    ]) {
      const html = await render(path);
      expect(html).not.toContain('href="/docs');
      for (const match of html.matchAll(
        /href="(\/vex\/docs[^"#]*)(?:#([^"]+))?"/g
      )) {
        const route = resolveRoute(match[1]);
        expect(route.kind, match[0]).toBe("docs");
        if (route.kind === "docs" && match[2]) {
          const target = docsPages.find((page) => page.slug === route.slug);
          expect(
            target?.sections.some((section) => section.id === match[2]),
            match[0]
          ).toBe(true);
        }
      }
    }
  });

  it("documents appearance, backups and clipboard trust", async () => {
    const configuration = await guide("configuration");
    for (const text of [
      "vex.config.appearance",
      "init.backup.lua",
      "one rotating backup, not a history",
      "&lt;name&gt;.backup.lua",
      "Saving through Settings replaces init.lua.",
      "Legacy JSON settings are no longer read."
    ])
      expect(configuration).toContain(text);
    expect(await guide("appearance")).toContain('id="terminal-colors"');
    expect(await guide("shortcuts")).toContain(
      "Clipboard read/query access is not enabled."
    );
  });

  it("documents the bounded recursive path index", async () => {
    const shortcuts = await guide("shortcuts");
    for (const text of [
      "Bounded recursive search.",
      "16,384",
      "8 MiB",
      "OSC 7"
    ])
      expect(shortcuts).toContain(text);
    expect(shortcuts).not.toContain("Local, not recursive.");
    expect(shortcuts).not.toContain("100,000");
  });

  it("documents native packaging and workspace persistence limitations", async () => {
    const installation = await guide("getting-started");
    for (const file of [
      "Vex.exe",
      "ghostty-vt.dll",
      "conpty.dll",
      "OpenConsole.exe"
    ])
      expect(installation).toContain(file);
    expect(await guide("workspace")).toContain(
      "does not provide project grouping, saved layout templates, or session restoration"
    );
  });
});

describe("routing and static rendering", () => {
  it("prerenders every route without browser globals", async () => {
    expect(staticPages).toHaveLength(docsPages.length + 2);
    for (const page of staticPages) {
      const html = await render(page.path);
      expect(html).toContain('id="main-content"');
      expect(html.match(/<h1[ >]/g)).toHaveLength(1);
    }
  });

  it("maps legacy bookmarks to canonical routes", () => {
    expect(resolveRoute("/").path).toBe(homePath);
    expect(resolveRoute("/docs").path).toBe(guidePath("getting-started"));
    expect(resolveRoute(docsPath).path).toBe(guidePath("getting-started"));
    for (const path of legacyPaths) {
      const canonical = resolveRoute(path);
      expect(resolveRoute(`${canonical.path}/`)).toEqual(canonical);
      expect(canonical.kind).not.toBe("not-found");
    }
  });

  it("does not render screenshots or application mockups", async () => {
    const html = await render(homePath);
    expect(html).not.toContain("<img");
    expect(html).not.toContain("/shots/");
    expect(html).not.toContain('role="tablist"');
  });

  it("renders unknown and nested routes as not-found pages", async () => {
    for (const path of [
      "/missing",
      "/vex/docs/missing",
      "/vex/docs/workspace/missing",
      "/docs/missing"
    ])
      expect(await render(path)).toContain("That page isn’t here.");
  });
});
