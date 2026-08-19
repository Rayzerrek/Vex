import {
  createRootRoute,
  createRoute,
  createRouter
} from "@tanstack/react-router";
import { DownloadPage, HomePage } from "./app.tsx";
import { RootLayout } from "./root-layout.tsx";
import { ThemeWorkspace } from "./theme-workspace.tsx";

const rootRoute = createRootRoute({ component: RootLayout });

const homeRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/",
  component: HomePage
});

const downloadRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/download",
  component: DownloadPage
});

const themesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/themes",
  component: ThemeWorkspace
});

const routeTree = rootRoute.addChildren([homeRoute, downloadRoute, themesRoute]);

export const router = createRouter({ routeTree });

declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router;
  }
}
