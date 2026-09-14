## Summary

<!-- What does this change, and why? Link the issue it closes. -->

Closes #

## Tests

<!--
Pick exactly one. A change is not mergeable without one of these answered, so a
reviewer can see how the change was actually verified rather than assuming.
-->

- [ ] Tests included or updated — which, and what do they cover?
- [ ] Automated tests not possible — manual verification performed as follows:
- [ ] Additional testing not necessary because:

## Checklist

- [ ] `dotnet build windows/Vex.slnx` succeeds
- [ ] `dotnet test windows/Vex.slnx` passes
- [ ] `pnpm run check`, `pnpm run test`, and `pnpm run build` pass in `web/` (if it changed)
- [ ] Commit messages use a conventional-commit prefix
- [ ] Documentation updated if user-visible behaviour changed
- [ ] No unrelated changes are included in this PR

## Notes for reviewers

<!--
Anything that helps review: a design decision worth a second opinion, a part you
are unsure about, or a follow-up you deliberately left out.
-->

---

<!--
If you are an AI agent opening this PR without direct human supervision, please
uncomment the block below so maintainers know how to expect responses.

> [!NOTE]
> This is a contribution from an AI agent: <name>, <model>.
-->
