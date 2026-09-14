import {
  createRootRoute,
  createRoute,
  createRouter,
  type RouterHistory
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

/** Builds an isolated router. Tests use this with a memory history so each
 * case gets a clean navigation stack instead of sharing the app singleton. */
export function createAppRouter(history?: RouterHistory) {
  return createRouter({ routeTree, ...(history ? { history } : {}) });
}

export const router = createAppRouter();

declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router;
  }
}
