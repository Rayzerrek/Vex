import { expect, test } from "@playwright/test";

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

test("home, theme persistence, and documentation navigation", async ({
  page
}, testInfo) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  page.on("console", (message) => {
    if (message.type() === "error") errors.push(message.text());
  });
  await page.goto("/");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Room forthe real work."
  );
  await expect(
    page.locator("main img, .product-stage, .workspace-demo")
  ).toHaveCount(0);
  const dark = await page
    .locator("html")
    .evaluate((element) => element.classList.contains("dark"));
  await page
    .getByRole("button", {
      name: dark ? "Switch to light theme" : "Switch to dark theme"
    })
    .click();
  await page.reload();
  expect(
    await page
      .locator("html")
      .evaluate((element) => element.classList.contains("dark"))
  ).toBe(!dark);
  await page
    .getByRole("navigation", { name: "Main navigation", exact: true })
    .getByRole("link", { name: "Docs", exact: true })
    .click();
  await expect(page).toHaveURL(/\/docs\/getting-started$/);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Installation"
  );
  await page.keyboard.press("Control+k");
  const search = page.getByRole("searchbox", {
    name: "Search documentation guides"
  });
  await expect(search).toBeFocused();
  await search.fill("keyboard");
  await page
    .getByRole("navigation", { name: "Documentation", exact: true })
    .getByRole("link", { name: "Keyboard shortcuts", exact: true })
    .click();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Keyboard shortcuts"
  );
  await page.goBack();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Installation"
  );
  await page.screenshot({
    path: testInfo.outputPath("docs.png"),
    fullPage: true,
    scale: "css"
  });
  expect(errors).toEqual([]);
});

test("search finds sections and handles empty results and Escape", async ({
  page
}) => {
  await page.goto("/docs/getting-started");
  await page.keyboard.press("Control+k");
  const search = page.getByRole("searchbox");
  await search.fill("reload without restarting");
  await page
    .getByRole("navigation", { name: "Documentation", exact: true })
    .getByRole("link", { name: "Reload without restarting", exact: true })
    .click();
  await expect(page).toHaveURL(/\/docs\/configuration#reload$/);
  await expect(page.locator("#reload")).toBeInViewport();
  await page.keyboard.press("Control+k");
  await page.getByRole("searchbox").fill("no-such-guide");
  await expect(page.getByRole("status")).toContainText("No matching guides");
  await page.keyboard.press("Escape");
  if (
    await page
      .getByRole("button", { name: "Open documentation navigation" })
      .isVisible()
  ) {
    await expect(
      page.getByRole("button", { name: "Open documentation navigation" })
    ).toBeFocused();
  } else await expect(page.getByRole("searchbox")).toHaveValue("");
});

test("each guide loads directly with working anchors and no page overflow", async ({
  page
}, testInfo) => {
  for (const guide of guides) {
    await page.goto(`/docs/${guide}`);
    await expect(page.locator(".doc-article > h1")).toBeVisible();
    await expect(page).toHaveTitle(/— Vex Docs$/);
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth
      )
    ).toBe(true);
    if (testInfo.project.name === "mobile") {
      await expect(
        page.getByRole("button", { name: "Open documentation navigation" })
      ).toBeVisible();
      await page.locator(".mobile-toc summary").click();
      await page.locator(".mobile-toc a").first().click();
    } else {
      await page
        .getByRole("navigation", { name: "On this page", exact: true })
        .getByRole("link")
        .first()
        .click();
    }
    expect(new URL(page.url()).hash).not.toBe("");
  }
  await page.goto("/");
  await page.screenshot({
    path: testInfo.outputPath("home.png"),
    fullPage: true,
    scale: "css"
  });
  for (const width of [320, 760, 768, 1024, 1440, 1920]) {
    await page.setViewportSize({ width, height: 900 });
    for (const path of ["/", ...guides.map((guide) => `/docs/${guide}`)]) {
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

test("code examples copy exact text and show confirmation", async ({
  page,
  context,
  browserName
}) => {
  test.skip(browserName !== "chromium", "Clipboard permissions use Chromium.");
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await page.goto("/docs/configuration");
  const block = page.locator(".code-block").first();
  const code = await block.locator("code").innerText();
  await block.getByRole("button", { name: "Copy Lua code" }).click();
  await expect(block.getByRole("button")).toContainText("Copied");
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(code);
});

test("active table of contents follows the reading position", async ({
  page
}, testInfo) => {
  test.skip(testInfo.project.name === "mobile", "Desktop reading rail.");
  await page.goto("/docs/configuration");
  await page.locator("#reload").scrollIntoViewIfNeeded();
  await page.locator("#reload").evaluate((element) => element.scrollIntoView());
  await expect(page.locator('.docs-toc a[href="#reload"]')).toHaveAttribute(
    "aria-current",
    "location"
  );
});

test("documentation is prerendered and navigable without JavaScript", async ({
  browser
}, testInfo) => {
  const context = await browser.newContext({
    javaScriptEnabled: false,
    viewport:
      testInfo.project.name === "mobile"
        ? { width: 390, height: 844 }
        : { width: 1280, height: 900 }
  });
  try {
    const page = await context.newPage();
    for (const guide of guides) {
      await page.goto(`http://127.0.0.1:4173/docs/${guide}/`);
      await expect(page.locator(".doc-article > h1")).toBeVisible();
      await expect(page.locator(".doc-section").first()).toBeVisible();
      await expect(page).toHaveTitle(/— Vex Docs$/);
      await expect(
        page.getByRole("navigation", { name: "Documentation", exact: true })
      ).toBeVisible();
    }
  } finally {
    await context.close();
  }
});
