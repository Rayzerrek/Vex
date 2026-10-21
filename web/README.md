# Website

React, TypeScript and Vite. Use Node.js 24 and the pnpm version in `package.json`.
From `web/`:

```sh
pnpm install --frozen-lockfile
pnpm run dev
```

## Checks

```sh
pnpm run check
pnpm run test
pnpm run build
```

`check` runs Prettier, ESLint and TypeScript. Use `pnpm run format` to apply
formatting. Tests use Vitest; no global CLI is needed.
The build checks types and prerenders all public routes.

## Editing

- `src/features/docs/content.tsx`: user guides, section anchors and navigation metadata.
- `src/pages/`, `src/components/`: pages and shared UI.
- `src/app.tsx`: routing and metadata.
- `src/index.css`, `src/motion.css`: themes, layout and motion.
- `src/entry-server.tsx`, `scripts/prerender.mjs`: static route generation.

Keep guides aligned with the application source and `docs/`. Do not advertise
legacy WPF features such as saved layouts or session restoration.
Repository guides stay short; detailed feature explanations belong here.

Keep content/navigation readable without JavaScript and respect reduced motion.
IBM Plex fonts are bundled locally; their licenses ship under `public/licenses/`.
Captures in `public/shots/` are legacy assets, not current product screenshots.

## Deploy

Serve `dist/` on a static host with directory `index.html` resolution and
`404.html` for missing routes. Guides also have flat `.html` aliases for
extensionless hosts. No SPA fallback or application server is needed.
