import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import {
  legacyPaths,
  render,
  resolveRoute,
  staticPages
} from "../dist-ssr/entry-server.js";

const template = await readFile("dist/index.html", "utf8");
const escape = (text) =>
  text
    .replaceAll("&", "&amp;")
    .replaceAll('"', "&quot;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;");

for (const page of staticPages) {
  const html = template
    .replace(/<title>.*?<\/title>/, `<title>${escape(page.title)}</title>`)
    .replace(
      /(<meta\s+name="description"\s+content=")[^"]*("\s*\/>)/,
      `$1${escape(page.description)}$2`
    )
    .replace(
      '<div id="root"></div>',
      `<div id="root">${await render(page.path)}</div>`
    );
  const target =
    page.path === "/404"
      ? "dist/404.html"
      : resolve("dist", `.${page.path}`, "index.html");
  await mkdir(resolve(target, ".."), { recursive: true });
  await writeFile(target, html);
  // Some static servers resolve clean URLs to .html but do not redirect directories.
  // Ship both forms so direct guide links hydrate the same article in either case.
  if (page.path !== "/" && page.path !== "/404") {
    await writeFile(resolve("dist", `.${page.path}.html`), html);
  }
}

// Static hosts have no redirect middleware. Keep old bookmarks working without JS.
for (const path of legacyPaths) {
  const target = resolveRoute(path).path;
  const html = `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta http-equiv="refresh" content="0;url=${target}"><title>Redirecting — Vex</title></head><body><a href="${target}">Continue to Vex</a><script>location.replace(${JSON.stringify(target)} + location.search + location.hash)</script></body></html>`;
  const file = resolve("dist", `.${path}`, "index.html");
  await mkdir(resolve(file, ".."), { recursive: true });
  await writeFile(file, html);
  if (path !== "/") await writeFile(resolve("dist", `.${path}.html`), html);
}

// Keep only deployable files; the server bundle is a build-time tool.
await rm("dist-ssr", { recursive: true, force: true });
console.log(
  `Prerendered ${staticPages.length} pages. Documentation is readable without JavaScript.`
);
