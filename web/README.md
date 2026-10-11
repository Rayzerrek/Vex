# Vex web

The Vex website and documentation use React, TypeScript, and Vite+. IBM Plex
fonts are bundled locally; no font service or application server is required.

## Development

```sh
pnpm install --frozen-lockfile
pnpm run dev
```

## Verification

```sh
pnpm run check
pnpm run test -- --run
pnpm run build
pnpm exec playwright install chromium
pnpm run test:e2e
```

Browser tests cover desktop and mobile navigation, feature controls, theme
persistence, documentation search, direct links, and reading without JavaScript.
Run the build before browser tests: they exercise the production preview.

## Deployment

Deploy `dist/` to a static host. The build prerenders the homepage, `/docs/`,
every `/docs/<slug>/` route, and `404.html`. The host should serve directory
`index.html` files and use `404.html` for missing routes. No SPA fallback is
needed for the documented routes. Client-side navigation enhances these pages.

Documentation content and navigation live in `src/docs.tsx`; keep product facts
aligned with the repository's `docs/` and Windows application. The SVG brand mark
is in `public/vex-mark.svg`, with its inline equivalent in `src/app.tsx`.
