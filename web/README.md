# Vex web

The Vex website and documentation use React, TypeScript, and Vite+. IBM Plex
fonts are bundled locally; no font service or application server is required.

## Development

Use Node.js 24 and the pnpm version pinned in `package.json`.

```sh
pnpm install --frozen-lockfile
pnpm run dev
```

## Build and checks

```sh
pnpm run check
pnpm run build
```

The production build checks TypeScript and prerenders all public routes.

## Structure

```text
src/
├── app.tsx                    # Routing, page metadata, navigation behavior
├── components/                # Shared site layout and SVG icons
├── features/docs/
│   ├── content.tsx            # Articles and derived navigation metadata
│   ├── code-block.tsx         # Code examples with clipboard feedback
│   └── use-active-section.ts  # Reading-position tracking
├── pages/                     # Homepage, documentation layout, not-found page
├── index.css                  # Theme tokens, layout, responsive styles
├── motion.css                 # Entrance sequence and interaction motion
├── main.tsx                   # Browser entry and local font imports
└── entry-server.tsx           # Static rendering and route manifest
```

## Design

The homepage is typography-led, with no application screenshots, terminal
mockups, or simulated product interfaces. Cool neutral surfaces and a violet
accent reference the native Vex Obsidian theme. Both website themes have their
own tokens; switching them does not change the installed application's theme.

Documentation separates navigation, a readable article column, and a sticky
section index. On narrow screens navigation is collapsible, with an inline
section index. Guide search matches titles, descriptions, and section names;
matching sections link directly to their anchors. Code examples can be copied
with explicit success or failure feedback. Static articles and navigation remain
available without JavaScript, including on mobile.

Motion is intentionally selective: the homepage has one staggered headline
entrance and three one-shot scroll-reveal groups (feature introduction, native
section, and documentation invitation). A shared IntersectionObserver arms only
offscreen groups. Homepage anchor buttons use native smooth scrolling for pointer
input and keep scroll reveals active during the journey. Keyboard navigation and
reduced-motion changes reveal groups immediately; focus reveals its own group.
Pointer interactions use short interruptible transitions,
and state icons crossfade without changing their footprint. Mobile navigation enters from
its trigger and closes immediately. Keyboard actions and documentation navigation
stay instant. `prefers-reduced-motion` disables movement; no content depends on
animation support or JavaScript to become readable. Motion uses CSS only, without
an animation-library dependency or a continuous render loop.

No reference-site assets are used. Existing captures under `public/shots/` are
legacy assets and are not referenced by the website.

## Deployment

Deploy `dist/` to a static host. The build prerenders the homepage, `/docs/`,
every `/docs/<slug>/` route, and `404.html`. Each guide also has a flat `.html`
alias for static servers that resolve extensionless URLs without redirecting to
trailing slashes. The host should serve directory `index.html` files and use
`404.html` for missing routes. No SPA fallback is needed for the documented
routes. Client-side navigation enhances these pages.

Keep documentation aligned with `app/README.md`, `docs/`, and the native source.
Current documentation covers native packaging, tabs and panes, Lua preferences,
Theme Studio, custom Windows/WSL profiles, shortcuts, CLI launches, and program
status. Legacy WPF projects, JSON settings, saved layouts, and session restore
are not features of the current native application.
