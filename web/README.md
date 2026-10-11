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

## Design and screenshots

The homepage uses a centered reading column, restrained typography, and native
app screenshots, with no decorative side panel. Its editorial tone is inspired
by Superlogical; documentation uses separate navigation and reading columns
inspired by Rex. No reference-site assets are used.
Both website themes work independently of the light/dark app screenshot preview.

`public/shots/` contains native Vex window captures at 1440 × 900: a three-pane
workspace in both appearances, the command palette, and the path picker. Shell
output is staged for reproducibility, not a benchmark or live test result.
Regenerate on Windows with PowerShell 7 and a built Vex executable:

```powershell
pwsh -NoProfile -File web/scripts/capture-app.ps1 -AppPath <path-to-Vex.App.exe>
```

Run from the repository root. Capture uses a disposable `VEX_PROFILE_DIR`; it
never edits the user's settings or restored sessions. It briefly focuses its own
window, then closes that process and removes the temporary profile.

To refresh the website captures in `screenshots/`, build and start the production
preview, then run the capture script from `web/` in another terminal:

```sh
pnpm run build
pnpm run preview -- --host 127.0.0.1 --port 4173
# In another terminal:
node scripts/capture-site.mjs
```

Captures cover the homepage and documentation on desktop/mobile, in both themes.
The script waits for local fonts and images and disables motion for stable output.
An alternative preview URL can be passed as its first argument.

## Deployment

Deploy `dist/` to a static host. The build prerenders the homepage, `/docs/`,
every `/docs/<slug>/` route, and `404.html`. The host should serve directory
`index.html` files and use `404.html` for missing routes. No SPA fallback is
needed for the documented routes. Client-side navigation enhances these pages.

Documentation content and navigation live in `src/docs.tsx`; keep product facts
aligned with the repository's `docs/` and Windows application. The SVG brand mark
is in `public/vex-mark.svg`, with its inline equivalent in `src/app.tsx`.
