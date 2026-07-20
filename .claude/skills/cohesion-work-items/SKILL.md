---
name: cohesion-work-items
description: Create and link GitHub work items (area epics / features / tasks) for assimalign/cohesion-platforms in the shared org Project #13 "Cohesion" using the gh CLI — following the L04.01.NN WBS scheme, native parent/sub-issue links, the Summary / Acceptance Criteria body template, and the Repo=cohesion-platforms project field. Use whenever new development is requested, and ESPECIALLY when scope creep is discovered mid-branch: file the out-of-scope work as its own tracked work item so one PR can attach and close several items. Triggers include "create a work item", "file an issue for this", "capture this scope creep", "track this extra change", "open a Cohesion issue", "this is out of scope — log it", or "assemble the Closes list for my PR". Use only in the assimalign/cohesion-platforms repo.
---

# Cohesion Work Items (cohesion-platforms)

Capture development as tracked GitHub work items in **Project #13 "Cohesion"** under the `assimalign` org,
using the `gh` CLI. Items for this repo are **issues on `assimalign/cohesion-platforms`** filed under
program root **`L04.01` (Cohesion - Deployment Platforms)** with the project's **`Repo` field set to
`cohesion-platforms`** (the cohesion repo's items use `L01.*`–`L03.*` and `Repo=cohesion`). The defining
use case is **scope-creep capture**: while implementing a feature you do extra work that falls outside the
original item; this skill turns that work into its own properly-placed issue so the eventual PR closes
*every* item it actually resolved — not just the one you started with.

Full schema, field IDs, and manual recipes live in [reference/project-schema.md](reference/project-schema.md).
The reliable end-to-end path is the script [scripts/New-CohesionWorkItem.ps1](scripts/New-CohesionWorkItem.ps1).

## The model (read this first)

Items carry their place in the title as `[<wbs>] <description>`; the tree is held by **native GitHub
parent/sub-issue links**; every item is added to Project #13.

| Code | Level | Example | Parent of new item |
| --- | --- | --- | --- |
| `L04.01.NN` (3 seg) | **Area epic** | `[L04.01.03] Platforms - Kubernetes` | parent for a new **feature** |
| `L04.01.NN.MM` (4 seg) | **Feature** | `[L04.01.03.02] …gateway skeleton` | parent for a new **task** |
| `L04.01.NN.MM.PP` (5 seg) | **Task** | `[L04.01.03.02.01] …` | leaf |

Your branch names the feature in flight: `feature/L04.01.03.02-kubernetes-gateway-skeleton` → feature `L04.01.03.02`.

## Step 1 — Classify the work before creating anything

When a piece of work surfaces, decide where it belongs. **This classification is the whole point** — getting
it right is what makes the WBS, the scope-creep accounting, and the multi-item PR meaningful. The script
infers an **Origin** from this choice so creep becomes measurable; the three buckets are:

- **In-scope of the current feature, but separable** → new **task** under the feature.
  Parent = the feature named by the branch. New code = `<branchWbs>.NN`. (`-As task`, the default.)
  → Origin **DiscoveredTask** — normal in-feature discovery.
- **Out of the current feature's scope, same area** → new **sibling feature** under the area epic.
  Parent = area epic (branch WBS minus its last segment). New code = `<areaWbs>.NN`. (`-As feature`.)
  → Origin **DiscoveredFeature** — a signal the original feature was under-scoped.
- **Different area entirely** → feature or task under *that* area's epic. Pass `-Parent` explicitly
  (find the area epic in [reference/project-schema.md](reference/project-schema.md)). Origin defaults to
  **Planned**; add `-Origin DiscoveredTask|DiscoveredFeature` if it is really creep.
- **Same intent as an existing item** → do **not** create a duplicate; reuse it.

If the classification is ambiguous, state your reasoning and ask the user which bucket it is before creating.

Before creating, **search for an existing item** so you don't duplicate — reuse beats re-filing. The script
ranks existing issues (open + closed) by title-word overlap:

```pwsh
.claude/skills/cohesion-work-items/scripts/New-CohesionWorkItem.ps1 -Search "websocket bootstrap http3"
```

If a strong match exists, add the work to it instead of creating a new item. You don't have to run `-Search`
separately: the create path **automatically** runs the same check and **blocks on a likely duplicate**
(open item, high overlap), printing the candidates — pass `-Force` only after you've confirmed the new item is
genuinely distinct.

## Step 2 — Create the work item (fast path)

Run the helper from the repo root. It infers the parent from the branch, computes the next free WBS child
code, creates the issue with a templated body, adds it to Project #13, sets fields, links the native
sub-issue, and records the item in a per-branch manifest for PR close-out.

```pwsh
# Scope-creep TASK on the current feature branch:
.claude/skills/cohesion-work-items/scripts/New-CohesionWorkItem.ps1 -As task `
  -Title "Validate kubeconfig context selection against in-cluster config" `
  -Summary "Hardening discovered while wiring the gateway skeleton; outside the original feature scope." `
  -Acceptance "Reject ambiguous contexts with an actionable error.","Add targeted unit tests.","NativeAOT-safe." `
  -Status "In progress"

# Out-of-feature → sibling FEATURE under the area epic:
.claude/skills/cohesion-work-items/scripts/New-CohesionWorkItem.ps1 -As feature `
  -Title "Implement image pull-secret rotation" -Wave W03 -Priority P003

# Different area, explicit parent (find the area epic in reference/project-schema.md):
.claude/skills/cohesion-work-items/scripts/New-CohesionWorkItem.ps1 -Parent L04.01.02 `
  -Title "Add digest verification to the OCI tarball store"

# Preview everything without creating (prints each gh command + the generated body):
.claude/skills/cohesion-work-items/scripts/New-CohesionWorkItem.ps1 -Title "..." -DryRun
```

Key options: `-Search "<keywords>"` (find existing items, then exit), `-As task|feature`,
`-Parent <issue#|WBS>`, `-Origin Planned|DiscoveredTask|DiscoveredFeature` (inferred when omitted), `-Title`,
`-Summary`, `-Acceptance a,b,c`, `-Standards a,b`, `-BodyFile <path>`, `-Status` (default `Backlog`),
`-Priority`, `-Wave`, `-Label`, `-Force` (override the duplicate block), `-DryRun`.

The script also: **searches for duplicates** and blocks on a likely match (Step 1); **validates placement**
(refuses a parent that isn't an area epic or feature, and refuses to infer off a non-feature branch); sets
**Kind / Area / Origin / Repo** project fields and the **`scope-creep`** label automatically; stamps discovered items
with a `> Discovered while implementing [<feature>] (#N)` provenance line; records each item (with its Origin)
in a per-branch manifest under `.git/cohesion/`; and **backs off and retries on GitHub rate limits**
(honoring `Retry-After` / the reset, else exponential backoff).

**Run `-DryRun` first** when you're unsure about the inferred parent or the next WBS code, then re-run for real.

If PowerShell isn't available, follow the **manual recipe** in
[reference/project-schema.md](reference/project-schema.md) — same six steps, raw `gh`/`gh api graphql`.

## Step 3 — Body content

The de-facto issue body is `## Summary` → `## Acceptance Criteria` → `### Standards and Compliance`.
The script generates this skeleton; fill it with real, testable criteria. For platform work, cite the
relevant spec in *Standards and Compliance* (OCI Distribution/Image spec, Docker Registry HTTP API v2,
Kubernetes API conventions); for runtime-contract work, note that no conformance suite applies.

## Step 4 — Close every item from the PR

GitHub auto-closes an issue only when the PR body has a **closing keyword + that issue's number**, and
`Closes #1, #2` on one line links only the first. Use **one keyword per line**. The script tracks every item
it created on this branch (with its Origin); emit the block from the **same worktree** when opening the PR
(the manifest lives in `.git/cohesion/`, so it is per-worktree):

```pwsh
.claude/skills/cohesion-work-items/scripts/New-CohesionWorkItem.ps1 -EmitClosesBlock
```

The output groups planned vs discovered work and tallies the creep — paste it into the PR description
(alongside the original feature item, which you close manually):

```
## Work items resolved by this PR
Closes #339

### Discovered (out-of-scope) work
Closes #714
Closes #720

<!-- creep: 2 of 3 items were discovered out-of-scope -->
```

Closing a parent feature does not close its sub-issues and vice-versa, so list each work item the PR resolves.

## Guardrails

- **Only operate on `assimalign/cohesion-platforms`** and Project #13. Confirm `gh auth status` has the `project` scope. Never file this repo's items as issues on `assimalign/cohesion`.
- **Never invent a WBS code** — always derive the next child from existing siblings (the script does this; if
  doing it by hand, list the parent's children and take max+1, zero-padded to two digits).
- **One concern per item.** If the creep is really several things, create several items.
- **Don't create duplicates** — search first (Step 1).
- **Confirm before creating** when the parent or classification is uncertain; prefer `-DryRun` to preview.
- **Set Status.** Use `In progress` if you're starting the work now, else `Backlog`/`Ready`.
- This skill is intake/tracking only — it does not change code. Follow the auto-loaded
  repo coding rules (`.claude/rules/`) for the actual implementation.
