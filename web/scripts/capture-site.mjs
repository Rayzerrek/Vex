import { chromium } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";

const baseURL = new URL(process.argv[2] ?? "http://127.0.0.1:4173");
const output = new URL("../screenshots/", import.meta.url);
await mkdir(output, { recursive: true });
const browser = await chromium.launch();

try {
  for (const viewport of [
    { name: "desktop", width: 1440, height: 1000 },
    { name: "mobile", width: 390, height: 844 }
  ]) {
    for (const theme of ["light", "dark"]) {
      const context = await browser.newContext({
        viewport: { width: viewport.width, height: viewport.height },
        colorScheme: theme,
        reducedMotion: "reduce",
        deviceScaleFactor: 1
      });
      try {
        const page = await context.newPage();
        const errors = [];
        page.on("pageerror", (error) => errors.push(error.message));
        page.on("console", (message) => {
          if (message.type() === "error") errors.push(message.text());
        });
        for (const [name, path] of [
          ["home", "/"],
          ["docs", "/docs/getting-started/"]
        ]) {
          await page.goto(new URL(path, baseURL).href, {
            waitUntil: "networkidle"
          });
          await page.evaluate(async () => {
            await document.fonts.ready;
            for (const image of document.images) image.loading = "eager";
            await Promise.all(
              Array.from(document.images, (image) => image.decode())
            );
          });
          if (errors.length > 0)
            throw new Error(`Vex site capture: ${errors.join("; ")}`);
          const filename = `${name}-${viewport.name}-${theme}`;
          await page.screenshot({
            path: fileURLToPath(new URL(`${filename}.png`, output)),
            fullPage: true
          });
          if (name === "home" && viewport.name === "desktop") {
            await page.screenshot({
              path: fileURLToPath(new URL(`${filename}-viewport.png`, output))
            });
          }
          console.log(`Captured ${filename}`);
        }
      } finally {
        await context.close();
      }
    }
  }
} finally {
  await browser.close();
}
