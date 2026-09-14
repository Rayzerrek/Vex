import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryHistory, RouterProvider } from "@tanstack/react-router";
import { createAppRouter } from "./router.tsx";

function renderAt(path: string) {
  const router = createAppRouter(
    createMemoryHistory({ initialEntries: [path] })
  );
  render(<RouterProvider router={router} />);
  return router;
}

afterEach(() => {
  cleanup();
  localStorage.clear();
  document.documentElement.classList.remove("dark");
});

describe("home page", () => {
  it("states the product in the hero heading", async () => {
    renderAt("/");

    expect(
      await screen.findByRole("heading", {
        level: 1,
        name: /native terminal workspace for windows/i
      })
    ).toBeDefined();
  });

  it("links to the GitHub repository and the download page", async () => {
    renderAt("/");

    const github = await screen.findAllByRole("link", { name: /github/i });
    expect(
      github.some(
        (a) => a.getAttribute("href") === "https://github.com/Rayzerrek/Vex"
      )
    ).toBe(true);

    const download = screen.getAllByRole("link", { name: /download/i });
    expect(download.some((a) => a.getAttribute("href") === "/download")).toBe(
      true
    );
  });

  it("shows the real screenshots with alternative text", async () => {
    renderAt("/");

    const shots = await screen.findAllByRole("img");
    const sources = shots.map((img) => img.getAttribute("src"));
    expect(sources).toContain("/shots/vex-light.png");
    expect(sources).toContain("/shots/vex-dark.png");
    expect(sources).toContain("/shots/vex-palette.png");

    for (const img of shots) {
      expect(img.getAttribute("alt")).toBeTruthy();
    }
  });

  it("documents the supported platform and runtime", async () => {
    renderAt("/");

    expect(await screen.findByText("Windows 10 1809+")).toBeDefined();
    expect(screen.getByText(".NET 10")).toBeDefined();
  });

  it("reports the GPL-3.0 license", async () => {
    renderAt("/");

    expect(await screen.findByText("GPL-3.0")).toBeDefined();
  });
});

describe("download page", () => {
  it("is reachable through the router", async () => {
    const router = renderAt("/download");

    expect(
      await screen.findByRole("heading", { level: 1, name: /start with vex/i })
    ).toBeDefined();
    expect(router.state.location.pathname).toBe("/download");
  });

  it("offers the installer and a source build", async () => {
    renderAt("/download");

    const installer = await screen.findByRole("link", {
      name: /windows installer/i
    });
    expect(installer.getAttribute("href")).toBe(
      "https://github.com/Rayzerrek/Vex/releases"
    );

    expect(
      screen.getByRole("link", { name: /build from source/i })
    ).toBeDefined();
  });

  it("never advertises MIT", async () => {
    // The project is GPL-3.0; the landing page must not claim otherwise.
    const { container } = render(
      <RouterProvider
        router={createAppRouter(
          createMemoryHistory({ initialEntries: ["/download"] })
        )}
      />
    );

    await screen.findByRole("heading", { level: 1, name: /start with vex/i });
    expect(container.textContent).not.toMatch(/\bMIT\b/);
    expect(container.textContent).toMatch(/GPL-3\.0/);
  });
});

describe("theme toggle", () => {
  it("switches the document to dark and persists the choice", async () => {
    const user = userEvent.setup();
    renderAt("/");

    const toggle = await screen.findByRole("button", {
      name: /switch to dark theme/i
    });
    await user.click(toggle);

    expect(document.documentElement.classList.contains("dark")).toBe(true);
    expect(localStorage.getItem("vex-theme")).toBe("dark");

    const back = screen.getByRole("button", { name: /switch to light theme/i });
    await user.click(back);

    expect(document.documentElement.classList.contains("dark")).toBe(false);
    expect(localStorage.getItem("vex-theme")).toBe("light");
  });

  it("exposes the toggle state to assistive technology", async () => {
    renderAt("/");

    const toggle = await screen.findByRole("button", {
      name: /switch to dark theme/i
    });
    expect(toggle.getAttribute("aria-pressed")).toBe("false");
  });
});

describe("navigation", () => {
  it("routes from the landing page to the download page", async () => {
    const user = userEvent.setup();
    const router = renderAt("/");

    const nav = await screen.findByRole("navigation", {
      name: /main navigation/i
    });
    await user.click(within(nav).getByRole("link", { name: /download/i }));

    expect(
      await screen.findByRole("heading", { level: 1, name: /start with vex/i })
    ).toBeDefined();
    expect(router.state.location.pathname).toBe("/download");
  });
});
