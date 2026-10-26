import { expect, test } from "@playwright/test";
import { docsPath, guidePath, homePath } from "../src/routes";

const guides = [
  "getting-started",
  "workspace",
  "configuration",
  "appearance",
  "shells",
  "shortcuts",
  "command-line",
  "program-status"
];

test("home and client-side guide navigation", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto(homePath);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Room forthe real work."
  );
  // A document request here would mean the router is not handling the link.
  let documents = 0;
  page.on("request", (request) => {
    if (request.isNavigationRequest()) documents++;
  });
  await page
    .getByRole("navigation", { name: "Main navigation", exact: true })
    .getByRole("link", { name: "Docs", exact: true })
    .click();
  await expect(page).toHaveURL(new RegExp(`${guidePath("getting-started")}$`));
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Installation"
  );
  await page.keyboard.press("Control+k");
  const search = page.getByRole("searchbox");
  await expect(search).toBeFocused();
  await search.fill("keyboard");
  await page
    .getByRole("navigation", { name: "Documentation", exact: true })
    .getByRole("link", { name: "Keyboard shortcuts", exact: true })
    .click();
  await expect(page).toHaveURL(new RegExp(`${guidePath("shortcuts")}$`));
  await expect(page).toHaveTitle("Keyboard shortcuts — Vex Docs");
  await page.goBack();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Installation"
  );
  expect(documents).toBe(0);
  expect(errors).toEqual([]);
});

test("website follows system color scheme", async ({ page }) => {
  await page.emulateMedia({ colorScheme: "light" });
  await page.goto(homePath);
  await expect(page.locator("html")).toHaveCSS("color-scheme", "light");
  await page.emulateMedia({ colorScheme: "dark" });
  await expect(page.locator("html")).toHaveCSS("color-scheme", "dark");
  await expect(
    page.getByRole("button", { name: /switch to .* theme/i })
  ).toHaveCount(0);
});

test("search finds sections, empty results and Escape", async ({ page }) => {
  await page.goto(docsPath);
  await page.keyboard.press("Control+k");
  await page.getByRole("searchbox").fill("reload without restarting");
  await page
    .getByRole("navigation", { name: "Documentation", exact: true })
    .getByRole("link", { name: "Reload without restarting", exact: true })
    .click();
  await expect(page).toHaveURL(
    new RegExp(`${guidePath("configuration")}#reload$`)
  );
  await expect(page.locator("#reload")).toBeInViewport();
  await page.keyboard.press("Control+k");
  await page.getByRole("searchbox").fill("no-such-guide");
  await expect(page.getByRole("status")).toContainText("No matching guides");
  await page.keyboard.press("Escape");
  const menu = page.getByRole("button", {
    name: "Open documentation navigation"
  });
  if (await menu.isVisible()) await expect(menu).toBeFocused();
  else await expect(page.getByRole("searchbox")).toHaveValue("");
});

test("legacy bookmarks redirect with query and hash", async ({ page }) => {
  await page.goto(`${docsPath}?source=bookmark#build-from-source`);
  await expect(page).toHaveURL(
    new RegExp(
      `${guidePath("getting-started")}\\?source=bookmark#build-from-source$`
    )
  );
  await page.goto("/docs/configuration?source=bookmark#reload");
  await expect(page).toHaveURL(
    new RegExp(`${guidePath("configuration")}\\?source=bookmark#reload$`)
  );
  await expect(page.locator("#reload")).toBeInViewport();
  await page.goto("/");
  await expect(page).toHaveURL(new RegExp(`${homePath}$`));
});

test("guides load directly, anchors work and layouts do not overflow", async ({
  page
}, testInfo) => {
  for (const slug of guides) {
    await page.goto(guidePath(slug));
    await expect(page.locator(".doc-article > h1")).toBeVisible();
    await expect(page).toHaveTitle(/— Vex Docs$/);
    if (testInfo.project.name === "mobile") {
      await page.locator(".mobile-toc summary").click();
      await page.locator(".mobile-toc a").first().click();
    } else
      await page
        .getByRole("navigation", { name: "On this page", exact: true })
        .getByRole("link")
        .first()
        .click();
    expect(new URL(page.url()).hash).not.toBe("");
  }
  for (const width of [320, 760, 768, 1024, 1440, 1920]) {
    await page.setViewportSize({ width, height: 900 });
    for (const path of [homePath, ...guides.map(guidePath)]) {
      await page.goto(path);
      expect(
        await page.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth
        ),
        `${path} at ${width}px`
      ).toBe(true);
    }
  }
});

test("code examples copy exact text and confirm", async ({ page, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await page.goto(guidePath("configuration"));
  const block = page.locator(".code-block").first();
  const code = await block.locator("code").innerText();
  await block.getByRole("button", { name: "Copy Lua code" }).click();
  await expect(block.getByRole("button")).toContainText("Copied");
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(code);
});

test("the final workspace sections remain navigable at the end of the page", async ({
  page
}, testInfo) => {
  test.skip(testInfo.project.name === "mobile", "Desktop reading rail.");
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(guidePath("workspace"));
  const toc = page.getByRole("navigation", { name: "On this page" });
  for (const id of ["closing", "persistence"]) {
    await toc.locator(`a[href="#${id}"]`).click();
    await expect(page).toHaveURL(new RegExp(`#${id}$`));
    await expect(toc.locator(`a[href="#${id}"]`)).toHaveAttribute(
      "aria-current",
      "location"
    );
    await expect(page.locator(`#${id}`)).toBeInViewport();
  }
});

test("active table of contents follows reading position", async ({
  page
}, testInfo) => {
  test.skip(testInfo.project.name === "mobile", "Desktop reading rail.");
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(guidePath("configuration"));
  await page.locator("#reload").evaluate((element) => element.scrollIntoView());
  await expect(page.locator('.docs-toc a[href="#reload"]')).toHaveAttribute(
    "aria-current",
    "location"
  );
});

test("documentation works without JavaScript", async ({
  browser
}, testInfo) => {
  const context = await browser.newContext({
    javaScriptEnabled: false,
    viewport:
      testInfo.project.name === "mobile"
        ? { width: 390, height: 844 }
        : { width: 1440, height: 900 }
  });
  try {
    const page = await context.newPage();
    for (const slug of guides) {
      await page.goto(`http://127.0.0.1:4173${guidePath(slug)}/`);
      await expect(page.locator(".doc-article > h1")).toBeVisible();
      await expect(page.locator(".doc-section").first()).toBeVisible();
      await expect(
        page.getByRole("navigation", { name: "Documentation", exact: true })
      ).toBeVisible();
    }
  } finally {
    await context.close();
  }
});

test("reduced motion disables entry and interaction transitions", async ({
  page
}) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.goto(homePath);
  expect(
    await page
      .locator(".page-enter")
      .evaluate((element) => getComputedStyle(element).transitionDuration)
  ).toBe("0s");
});
