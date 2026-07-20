## Summary

<!-- What does this PR do, and why? One or two short paragraphs. -->

## Type of change

<!-- Mark all that apply (conventional-commit types). -->

- [ ] `feat` — new feature
- [ ] `fix` — bug fix
- [ ] `refactor` — behavior-preserving change
- [ ] `test` — tests only
- [ ] `docs` — documentation only
- [ ] `chore` — build / tooling / CI

## Changes

<!-- The notable changes, as a short list. -->

-

## Work items resolved

<!--
List EVERY work item this PR closes, each on its OWN line with a closing keyword.
A single "Closes #1, #2" links only the FIRST issue — use one keyword per line.
Closing a feature does NOT close its sub-issues (and vice-versa), so list each one.
Do NOT put a closing keyword on items this PR does not actually resolve (e.g. backlog
items for deferred work) — link those with a plain "#123" instead.

Generate this block from your branch:
  .claude/skills/cohesion-work-items/scripts/New-CohesionWorkItem.ps1 -EmitClosesBlock
-->

Closes #

### Discovered (out-of-scope) work

<!-- Scope-creep items captured mid-branch (Origin = Discovered*). Remove this section if there were none. -->

## Testing & verification

<!-- How was this verified? Commands run, tests added, manual checks. -->

- [ ] `dotnet build` and the affected `dotnet test` projects pass locally.
-

## Checklist

- [ ] Each resolved work item (the parent feature **and** its sub-issues) has its own `Closes #` line; deferred/backlog items are linked without a closing keyword.
- [ ] Follows the repo coding rules (`.claude/rules/`): file-scoped namespaces; `CohesionProjectReference` / `CohesionPackageReference` (no raw references, no `Microsoft.Extensions.*`); interface-first with `internal` implementations; `extension(...)` members; area-scoped exception roots.
- [ ] Public APIs have XML docs; new libraries include `docs/OVERVIEW.md` and `docs/DESIGN.md`.
- [ ] No gratuitous reflection; serialization is source-generated where practical (this repo has no hard AOT mandate; sample/workload runtimes stay AOT-publishable).
- [ ] Tests added/updated and passing; any new project is wired into its area `.slnx` and a CI workflow.
- [ ] No dangling solution/project references left behind (renamed or moved projects updated everywhere).
