# Website development

Use Node.js 24 and the pnpm version pinned in `package.json`.
Run commands from `web/`, or use `pnpm --dir web` from the repository root.

```sh
pnpm install --frozen-lockfile
pnpm run check
pnpm run test
pnpm run build
```

- `pnpm run dev` starts Vite; `pnpm run preview` serves the production build.
- `pnpm run format` applies Prettier; `check` verifies formatting, ESLint and types.
- Tests use Vitest. The production build also prerenders every public route.
- Detailed user documentation lives in `src/features/docs/content.tsx`.
- Keep content readable without JavaScript and respect reduced motion.
