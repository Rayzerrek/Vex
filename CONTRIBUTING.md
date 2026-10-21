# Contributing

## Setup and checks

- Application: Zig/Python from `nix develop` on Linux/WSL, or the pinned Zig
  on Windows. See [app/README.md](app/README.md).
- Website: Node.js 24 and the pnpm version in `web/package.json`.
  See [web/README.md](web/README.md).
- Run the relevant checks in [docs/testing.md](docs/testing.md). State explicitly
  whether Windows runtime and desktop checks were performed; cross-compilation
  alone does not cover them.

## Changes

- Match the surrounding style; keep unrelated refactors separate.
- Add regression tests for behavior changes. Discuss new runtime dependencies first.
- Comments explain why, rather than restating code.
- Keep repository docs short and task-oriented. Detailed user guides belong in
  `web/src/features/docs/content.tsx`; historical evidence belongs in `docs/reviews/`.
- Start commits with `feat:`, `fix:`, `refactor:`, `perf:`, `docs:`, `chore:`,
  `ui:`, `vendor:`, `style:`, `test:`, `build:`, `ci:` or `revert:`;
  an optional scope is welcome.

## Releases

`app/VERSION` is the version source of truth. See
[CI and releases](app/README.md#ci-and-releases) for preview artifacts and stable tags.

## Reporting issues

Include the Vex version, Windows build, shell and reproduction steps.
For rendering issues, include the font, GPU and TUI application.
Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).
