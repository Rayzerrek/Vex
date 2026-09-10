import {
  createRootRoute,
  createRoute,
  createRouter
} from "@tanstack/react-router";
import { DownloadPage, HomePage } from "./app.tsx";
import { RootLayout } from "./root-layout.tsx";

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

const routeTree = rootRoute.addChildren([homeRoute, downloadRoute]);

export const router = createRouter({ routeTree });

declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router;
  }
}
