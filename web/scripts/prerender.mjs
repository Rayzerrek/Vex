import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { render, staticPages } from "../dist-ssr/entry-server.js";

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
      `<div id="root">${render(page.path)}</div>`
    );
  const target =
    page.path === "/404"
      ? "dist/404.html"
      : resolve("dist", `.${page.path}`, "index.html");
  await mkdir(resolve(target, ".."), { recursive: true });
  await writeFile(target, html);
}

// Keep only deployable files; the server bundle is a build-time tool.
await rm("dist-ssr", { recursive: true, force: true });
console.log(
  `Prerendered ${staticPages.length} pages. Documentation is readable without JavaScript.`
);
