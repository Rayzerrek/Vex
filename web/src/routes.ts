export const repository = "https://github.com/Rayzerrek/Vex";
export const homePath = "/vex";
export const docsPath = "/vex/docs";

export const guidePaths: Record<string, string> = {
  "getting-started": "installation",
  workspace: "workspace",
  configuration: "customize/config",
  appearance: "customize/appearance",
  shells: "customize/shells",
  shortcuts: "customize/keyboard-shortcuts",
  "command-line": "automate/cli",
  "program-status": "build/program-status"
};

export function guidePath(slug: string) {
  return `${docsPath}/${guidePaths[slug] ?? slug}`;
}

export function resolveRoute(path: string) {
  const normalized = path.replace(/\/+$/, "") || "/";
  if (normalized === "/" || normalized === homePath)
    return { kind: "home", path: homePath } as const;
  if (normalized === docsPath || normalized === "/docs")
    return { kind: "docs", path: docsPath, slug: "getting-started" } as const;
  for (const [slug, segment] of Object.entries(guidePaths)) {
    if (
      normalized === `${docsPath}/${segment}` ||
      normalized === `/docs/${slug}`
    )
      return { kind: "docs", path: guidePath(slug), slug } as const;
  }
  return { kind: "not-found", path: normalized } as const;
}

export const legacyPaths = [
  "/",
  "/docs",
  ...Object.keys(guidePaths).map((slug) => `/docs/${slug}`)
];
