import { describe, expect, it } from "vite-plus/test";
import { renderToStaticMarkup } from "react-dom/server";
import { DocContent, docsPages } from "./features/docs/content";
import { render, staticPages } from "./entry-server";

describe("documentation", () => {
  it("has unique routes and matching section anchors", () => {
    expect(new Set(docsPages.map((page) => page.slug)).size).toBe(
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

  it("only links to existing documentation pages and sections, including the homepage", () => {
    for (const path of [
      "/",
      ...docsPages.map((page) => `/docs/${page.slug}`)
    ]) {
      for (const match of render(path).matchAll(
        /href="\/docs\/([^"#]+)(?:#([^"]+))?"/g
      )) {
        const target = docsPages.find(
          (candidate) => candidate.slug === match[1]
        );
        expect(target, match[0]).toBeDefined();
        if (match[2])
          expect(
            target?.sections.some((section) => section.id === match[2]),
            match[0]
          ).toBe(true);
      }
    }
  });

  it("documents native packaging and the absence of legacy workspace persistence", () => {
    const installation = render("/docs/getting-started");
    for (const file of [
      "Vex.exe",
      "ghostty-vt.dll",
      "conpty.dll",
      "OpenConsole.exe"
    ])
      expect(installation).toContain(file);
    expect(render("/docs/workspace")).toContain(
      "does not provide project grouping, saved layout templates, or session restoration"
    );
    expect(render("/docs/configuration")).toContain(
      "Saving through Settings replaces init.lua."
    );
    expect(render("/docs/configuration")).toContain(
      "Legacy JSON settings are no longer read."
    );
  });
});

describe("static rendering", () => {
  it("renders every route without browser globals", () => {
    expect(staticPages).toHaveLength(docsPages.length + 3);
    for (const page of staticPages) {
      const html = render(page.path);
      expect(html).toContain('id="main-content"');
      expect(html.match(/<h1[ >]/g)).toHaveLength(1);
    }
  });
  it("does not render screenshots or application mockups", () => {
    const html = render("/");
    expect(html).not.toContain("<img");
    expect(html).not.toContain("/shots/");
    expect(html).not.toContain('role="tablist"');
  });
  it("renders unknown and nested routes as not-found pages", () => {
    for (const path of ["/missing", "/docs/missing", "/docs/workspace/missing"])
      expect(render(path)).toContain("That page isn’t here.");
  });
});
