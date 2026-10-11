import { describe, expect, it } from "vite-plus/test";
import { renderToStaticMarkup } from "react-dom/server";
import { DocContent, docsPages } from "./docs";
import { render, staticPages } from "./entry-server";

describe("documentation", () => {
  it("has unique routes and matching section anchors", () => {
    expect(new Set(docsPages.map((page) => page.slug)).size).toBe(6);
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

  it("only links to existing documentation pages and sections", () => {
    for (const page of docsPages) {
      const html = renderToStaticMarkup(<DocContent slug={page.slug} />);
      for (const match of html.matchAll(
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

  it("keeps installation and restoration limitations explicit", () => {
    expect(render("/docs/getting-started")).toContain("win-x64.msi");
    expect(render("/docs/getting-started")).toContain("ghostty-vt.dll");
    expect(render("/docs/workspace")).toContain(
      "Running commands and program state are not resumed."
    );
  });
});

describe("static rendering", () => {
  it("renders every route without browser globals", () => {
    expect(staticPages).toHaveLength(9);
    for (const page of staticPages) {
      const html = render(page.path);
      expect(html).toContain('id="main-content"');
      expect(html.match(/<h1[ >]/g)).toHaveLength(1);
    }
  });

  it("renders unknown routes as helpful not-found pages", () => {
    expect(render("/missing")).toContain("That page isn’t here.");
    expect(render("/docs/missing")).toContain("That page isn’t here.");
  });
});
