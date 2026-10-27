import {
  createRootRoute,
  createRoute,
  createRouter,
  lazyRouteComponent,
  Outlet,
  redirect,
  type RouterHistory
} from "@tanstack/react-router";
import { RouteLayout } from "./components/site-layout";
import { docsPath, guidePaths, legacyPaths, resolveRoute } from "./routes";

export function createSiteRouter(history?: RouterHistory) {
  const root = createRootRoute({
    component: RouteLayout,
    notFoundComponent: lazyRouteComponent(
      () => import("./pages/not-found"),
      "NotFound"
    )
  });
  const vex = createRoute({
    getParentRoute: () => root,
    path: "vex",
    component: Outlet
  });
  const home = createRoute({
    getParentRoute: () => vex,
    path: "/",
    component: lazyRouteComponent(() => import("./pages/home"), "Home")
  });
  const introDemo = createRoute({
    getParentRoute: () => vex,
    path: "intro-demo",
    component: lazyRouteComponent(
      () => import("./pages/intro-demo"),
      "IntroDemo"
    )
  });
  const docs = createRoute({
    getParentRoute: () => vex,
    path: "docs",
    component: Outlet
  });
  const docsComponent = lazyRouteComponent(
    () => import("./pages/docs"),
    "DocsRoute"
  );
  const guides = Object.values(guidePaths).map((path) =>
    createRoute({
      getParentRoute: () => docs,
      path,
      component: docsComponent
    })
  );
  const docsIndex = createRoute({
    getParentRoute: () => docs,
    path: "/",
    beforeLoad: ({ location }) => {
      throw redirect({
        to: resolveRoute(docsPath).path,
        hash: location.hash,
        search: location.search,
        replace: true
      });
    }
  });
  const aliases = legacyPaths
    .filter((path) => path !== docsPath)
    .map((path) =>
      createRoute({
        getParentRoute: () => root,
        path,
        beforeLoad: ({ location }) => {
          throw redirect({
            to: resolveRoute(path).path,
            hash: location.hash,
            search: location.search,
            replace: true
          });
        }
      })
    );
  return createRouter({
    routeTree: root.addChildren([
      vex.addChildren([
        home,
        introDemo,
        docs.addChildren([docsIndex, ...guides])
      ]),
      ...aliases
    ]),
    history,
    defaultPreload: "intent",
    scrollRestoration: true,
    defaultNotFoundComponent: lazyRouteComponent(
      () => import("./pages/not-found"),
      "NotFound"
    )
  });
}
