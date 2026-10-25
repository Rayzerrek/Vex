# Website

React, TypeScript, TanStack Router, Tailwind CSS 4 and Vite. Use Node.js 24
and the pnpm version pinned in `package.json`.

```sh
pnpm install --frozen-lockfile
pnpm run dev
```

## Verify

```sh
pnpm run check
pnpm run test
pnpm run build
pnpm run test:e2e
```

`check` runs Prettier, ESLint and TypeScript. `build` prerenders every public
route. Playwright runs against that production build; install Chromium first
with `pnpm exec playwright install chromium` if needed.

## Structure

- `src/router.ts`: nested TanStack routes, lazy page loading and legacy redirects.
- `src/routes.ts`: shared paths and canonical URL resolution.
- `src/components/`: site header, footer, branding, theme and layout.
- `src/pages/`: home, documentation and not-found page composition.
- `src/features/docs/`: guide content, sidebar search, contents rail, pagination and code blocks.
- `src/index.css`: Tailwind tokens, reusable content styles and small CSS transitions.
- `src/entry-server.tsx`, `scripts/prerender.mjs`: build-time static rendering.

The product lives at `/vex`, with guides under `/vex/docs`. Customization,
automation and integration guides use grouped paths. Legacy `/` and `/docs/...`
bookmarks redirect to their canonical equivalents, preserving query and hash.

Keep guides aligned with the native application and repository `docs/`. Do not
advertise legacy WPF features such as saved layouts or session restoration.
Content and navigation must work without JavaScript. Animations respect reduced
motion and keyboard navigation. Phosphor icons use direct imports; locally
bundled IBM Plex fonts ship with licenses in `public/licenses/`.

## Deploy

Serve `dist/` on a static host with directory `index.html` resolution and
`404.html` for missing routes. Guides also ship flat `.html` aliases for
extensionless hosts. No SPA fallback or application server is required.
