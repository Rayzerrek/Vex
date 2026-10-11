import { expect, test } from "@playwright/test";

const guides = [
  "getting-started",
  "workspace",
  "shortcuts",
  "appearance",
  "configuration",
  "program-status"
];

test("home, feature controls, theme persistence, and docs navigation", async ({
  page
}, testInfo) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  page.on("console", (message) => {
    if (message.type() === "error") errors.push(message.text());
  });
  await page.goto("/");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Your terminals.One workspace."
  );
  await page.getByRole("tab", { name: "Path picker" }).click();
  await expect(page.getByRole("tabpanel")).toContainText(
    "Bring the right path to the prompt."
  );
  await expect(page.getByRole("tabpanel").getByRole("img")).toHaveAttribute(
    "src",
    "/shots/vex-path-picker.png"
  );
  await page.getByRole("tab", { name: "Program status" }).click();
  await expect(page.getByRole("tabpanel")).toContainText("Approval requested");
  await page.getByRole("tab", { name: "Program status" }).press("ArrowRight");
  await expect(
    page.getByRole("tab", { name: "Projects & panes" })
  ).toBeFocused();
  const dark = await page
    .locator("html")
    .evaluate((element) => element.classList.contains("dark"));
  await page
    .getByRole("button", {
      name: dark ? "Switch to light theme" : "Switch to dark theme"
    })
    .click();
  await page.reload();
  await expect(page.locator("html")).toHaveClass(dark ? "" : "dark");
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
    .getByRole("link", { name: "Keyboard shortcuts" })
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

test("each guide loads directly with a title, working anchors, and no page overflow", async ({
  page
}, testInfo) => {
  for (const guide of guides) {
    await page.goto(`/docs/${guide}`);
    await expect(page.locator(".doc-article > h1")).toBeVisible();
    await expect(page).toHaveTitle(/— Vex Docs$/);
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= window.innerWidth
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
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth
    )
  ).toBe(true);
  await page.screenshot({
    path: testInfo.outputPath("home.png"),
    fullPage: true,
    scale: "css"
  });
  await page.setViewportSize({ width: 320, height: 740 });
  for (const path of ["/", ...guides.map((guide) => `/docs/${guide}`)]) {
    await page.goto(path);
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= window.innerWidth
      )
    ).toBe(true);
  }
});

test("screenshot appearances load independently of the website theme", async ({
  page
}) => {
  await page.goto("/");
  const preview = page.locator(".product-stage img");
  const appearances = page.getByRole("group", {
    name: "Screenshot appearance"
  });
  for (const appearance of ["Light", "Dark"]) {
    await appearances
      .getByRole("button", { name: appearance, exact: true })
      .click();
    await expect(preview).toHaveAttribute(
      "src",
      `/shots/vex-${appearance.toLowerCase()}.png`
    );
    await expect(
      appearances.getByRole("button", { name: appearance, exact: true })
    ).toHaveAttribute("aria-pressed", "true");
    await expect
      .poll(() =>
        preview.evaluate(
          (image) =>
            image instanceof HTMLImageElement &&
            image.complete &&
            image.naturalWidth === 1440
        )
      )
      .toBe(true);
  }
  await page.getByRole("button", { name: "Switch to dark theme" }).click();
  await expect(preview).toHaveAttribute("src", "/shots/vex-dark.png");
  for (const width of [320, 760, 768, 1024, 1440, 1920]) {
    await page.setViewportSize({ width, height: 900 });
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth
      )
    ).toBe(true);
    await expect(page.locator(".atmosphere-panel")).toHaveCount(0);
    const content = await page.locator(".hero").boundingBox();
    expect(content).not.toBeNull();
    if (content)
      expect(Math.abs(content.x + content.width / 2 - width / 2)).toBeLessThan(
        1
      );
  }
});

test("documentation is prerendered and readable without JavaScript", async ({
  browser
}) => {
  const context = await browser.newContext({ javaScriptEnabled: false });
  const page = await context.newPage();
  for (const guide of guides) {
    await page.goto(`http://127.0.0.1:4173/docs/${guide}/`);
    await expect(page.locator(".doc-article > h1")).toBeVisible();
    await expect(page.locator(".doc-section").first()).toBeVisible();
    await expect(page).toHaveTitle(/— Vex Docs$/);
  }
  await context.close();
});
