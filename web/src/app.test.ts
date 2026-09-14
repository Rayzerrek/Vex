import { readFileSync, existsSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// The landing page is static, so the failures worth catching are content
// failures: a screenshot path that no longer resolves, a link that points
// nowhere, or a license claim that disagrees with the LICENSE file. All three
// have happened or nearly happened here, and none of them need a DOM to detect.

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, "..");
const repoRoot = resolve(webRoot, "..");

const read = (path: string) => readFileSync(resolve(repoRoot, path), "utf8");
const app = read("web/src/app.tsx");
const router = read("web/src/router.tsx");
const readme = read("README.md");
const license = read("LICENSE");

describe("assets", () => {
  it("every screenshot referenced by the page exists", () => {
    const referenced = [...app.matchAll(/\/shots\/[A-Za-z0-9._-]+/g)].map(
      (m) => m[0]
    );

    expect(referenced.length).toBeGreaterThan(0);

    const missing = referenced.filter(
      (ref) => !existsSync(resolve(webRoot, "public", ref.replace(/^\//, "")))
    );

    expect(missing).toEqual([]);
  });

  it("every screenshot has alternative text", () => {
    const images = [...app.matchAll(/<img\b[\s\S]*?\/>/g)].map((m) => m[0]);

    expect(images.length).toBeGreaterThan(0);

    const withoutAlt = images.filter((tag) => !/\balt=/.test(tag));
    expect(withoutAlt).toEqual([]);
  });
});

describe("licensing", () => {
  it("never advertises a license other than GPL-3.0", () => {
    // The page claimed MIT while LICENSE was GPL-3.0. Guard the regression.
    expect(app).not.toMatch(/\bMIT\b/);
    expect(app).toMatch(/GPL-3\.0/);
  });

  it("agrees with the LICENSE file", () => {
    expect(license).toMatch(/GNU GENERAL PUBLIC LICENSE/);
    expect(license).toMatch(/Version 3, 29 June 2007/);
    expect(readme).toMatch(/GPL-3\.0/);
  });
});

describe("navigation", () => {
  it("declares the routes the page links to", () => {
    const declared = [...router.matchAll(/path:\s*"([^"]+)"/g)].map(
      (m) => m[1]
    );

    expect(declared).toContain("/");
    expect(declared).toContain("/download");

    // Every internal target used by the page must be a declared route.
    const internal = [...app.matchAll(/to="(\/[A-Za-z0-9/_-]*)"/g)].map(
      (m) => m[1]
    );
    const undeclared = internal.filter((to) => !declared.includes(to));

    expect(undeclared).toEqual([]);
  });

  it("points the download actions at the GitHub repository", () => {
    const hrefs = [...app.matchAll(/href:\s*"([^"]+)"/g)].map((m) => m[1]);

    expect(hrefs).toContain("https://github.com/Rayzerrek/Vex/releases");
    expect(hrefs).toContain("https://github.com/Rayzerrek/Vex");
  });
});

describe("page shell", () => {
  it("shows the platform and runtime the app supports", () => {
    expect(app).toMatch(/Windows 10 1809\+/);
    expect(app).toMatch(/\.NET 10/);
  });

  it("mounts into a root element that index.html provides", () => {
    const html = read("web/index.html");
    expect(app).toBeDefined();
    expect(html).toMatch(/id="root"/);
    expect(html).toMatch(/src="\/src\/main\.tsx"/);
  });
});
