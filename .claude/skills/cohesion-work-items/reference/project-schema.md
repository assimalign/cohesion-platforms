# Cohesion GitHub Project — schema & manual recipes (cohesion-platforms)

Reference for the `cohesion-work-items` skill in the **cohesion-platforms** repo. The project is the
same shared org project the cohesion repo uses; only the issue repo, the WBS program root, and the
`Codebase` field value differ. The helper script resolves IDs **dynamically**, so the ids here are for
the manual path and for understanding the model. If an ID stops working, re-run the discovery
commands at the bottom.

## Coordinates

| Thing | Value |
| --- | --- |
| Repo (issues live here) | `assimalign/cohesion-platforms` |
| Org / owner | `assimalign` |
| Project | **#13 "Cohesion"** (shared with the cohesion repo) |
| Project node id | `PVT_kwDOA9eCcc4AwTRy` |
| Program root | `L04.01` — Cohesion - Deployment Platforms |
| `Codebase` field value for this repo's items | `cohesion-platforms` |

## WBS taxonomy

Work items carry their position in the title as `[<code>] <description>`. The hierarchy is held by
**native GitHub parent/sub-issue links**, and every item is added to Project #13.

| Code shape | Segments | Level | Example | Parent |
| --- | --- | --- | --- | --- |
| `L04.01.00` | 3 (`.00`) | Program root | `[L04.01.00] Cohesion - Deployment Platforms` | — |
| `L04.01.NN` | 3 | **Area epic** | `[L04.01.03] Platforms - Kubernetes` | program root |
| `L04.01.NN.MM` | 4 | **Feature** | `[L04.01.03.02] Kubernetes gateway skeleton…` | area epic |
| `L04.01.NN.MM.PP` | 5 | **Task** | `[L04.01.03.02.01] …` | feature |

`L04.01` = Deployment Platforms program (cohesion repo programs are `L01.01` Foundation Libraries,
`L01.02` SDK/Tooling/Delivery, `L02.01` Application Runtime, `L03.0x` product platforms). Area epics
are titled `Platforms - <Area>`.

Branch convention: `feature/<wbs>-<slug>` (e.g. `feature/L04.01.03.02-kubernetes-gateway-skeleton`).
The WBS in the branch names the **feature** currently in flight.

### Current area epics (parents for new sibling features)

<!-- AREA-EPICS:BEGIN — created 2026-07-20; re-list with the command below -->
| Issue | Code | Area |
| --- | --- | --- |
| #2 | L04.01.01 | Delivery |
| #7 | L04.01.02 | Containers |
| #14 | L04.01.03 | Kubernetes |
| #23 | L04.01.04 | Docker |
<!-- AREA-EPICS:END -->

Program root: **#1 `[L04.01.00] Cohesion - Deployment Platforms`**.

(Re-list with: `gh issue list --repo assimalign/cohesion-platforms --state open --search '"Platforms -" in:title' --json number,title`)

## Custom fields (single-select)

Same project as the cohesion repo, so the field/option ids match the cohesion skill's reference. The
script resolves these **by name** at runtime.

| Field | Field id | Options (name = optionId) |
| --- | --- | --- |
| **Status** | `PVTSSF_lADOA9eCcc4AwTRyzgmmAUg` | Backlog=`f75ad846`, Ready=`08afe404`, In progress=`47fc9ee4`, In review=`4cc61d42`, Done=`98236657` |
| **Priority** | `PVTSSF_lADOA9eCcc4AwTRyzgmmAXc` | P001=`b310d11b` … P007=`3637977d` |
| **Wave** | `PVTSSF_lADOA9eCcc4AwTRyzhBivEo` | W01=`e74c191b`, W02=`9fbf32aa`, W03=`c8f13de9`, W04=`8db9e325`, W05=`e04894c2`, W06=`c1ccc362` |
| **Kind** | `PVTSSF_lADOA9eCcc4AwTRyzhWf6Os` | Program=`2fe515c1`, Area Epic=`50b6b808`, Feature=`5e827738`, Task=`9d3e180d` |
| **Area** | `PVTSSF_lADOA9eCcc4AwTRyzhWf6Jc` | cohesion foundation areas only. **Platform items leave Area UNSET** — `updateProjectV2Field` regenerates every option id and wipes existing selections (verified 2026-07-20), so platform options were deliberately not added. Group platform items by `Codebase` + the WBS title prefix instead. |
| **Origin** | `PVTSSF_lADOA9eCcc4AwTRyzhWf6JY` | Planned=`19e7b7e6`, DiscoveredTask=`89001270`, DiscoveredFeature=`9d353cbc` |
| **Codebase** | `PVTSSF_lADOA9eCcc4AwTRyzhYZcME` | cohesion=`4028c6a8`, cohesion-platforms=`ec8d54bf` — separates the two repos' requirements on the shared board ("Repo" is a reserved field name) |

The script sets **Status, Kind, Origin, Codebase** on every item it creates (plus Priority/Wave when
passed). `Kind` comes from the WBS depth (Feature/Task), `Origin` from the scope-creep classification,
`Codebase` is always `cohesion-platforms` here.

## Body template (same de-facto standard as the cohesion repo)

```markdown
## Summary
- <one or two sentences: what and why. For scope-creep items, note it was discovered out of scope.>

## Acceptance Criteria
- <observable, testable outcome>
- Tests cover the new behavior.
- No gratuitous reflection; serialization is source-generated where practical.

### Standards and Compliance
- <OCI Distribution/Image spec, Docker Registry HTTP API v2, or Kubernetes API conventions where applicable; else note it is a runtime-contract concern>
```

## Manual recipe (when not using the helper script)

```bash
REPO=assimalign/cohesion-platforms ; OWNER=assimalign ; PROJ=13
PROJECT_ID=PVT_kwDOA9eCcc4AwTRy

# 1. Find the parent + its node id (feature for a task, area epic for a sibling feature)
gh issue view <parent#> --repo $REPO --json number,title,id

# 2. Find the next free child number. Do NOT use --search for the dotted code (it silently drops
#    siblings). Fetch all issues and filter on the title with gh's built-in jq (-q):
gh issue list --repo $REPO --state all --limit 5000 --json number,title \
  -q '.[] | select(.title | test("^\\[L04\\.01\\.03\\.02\\.[0-9]{2}\\]")) | .title'
#    Then take the max trailing NN across OPEN and CLOSED, add 1, zero-pad to two digits.

# 3. Create the issue
URL=$(gh issue create --repo $REPO \
  --title '[L04.01.03.02.01] <short imperative description>' \
  --body-file body.md)
NUM=${URL##*/}

# 4. Add to project, capture the project item id
ITEM=$(gh project item-add $PROJ --owner $OWNER --url "$URL" --format json --jq .id)

# 5. Set fields (Status, Kind, Origin, Codebase, [Priority, Wave]) — resolve option ids by name:
gh project field-list $PROJ --owner $OWNER --format json \
  --jq '.fields[] | select(.name=="Codebase") | {id, options:[.options[]|{name,id}]}'
gh project item-edit --id "$ITEM" --project-id $PROJECT_ID \
  --field-id <fieldId> --single-select-option-id <optionId>

# 6. Link as a native sub-issue of the parent
PARENT_ID=$(gh issue view <parent#> --repo $REPO --json id --jq .id)
CHILD_ID=$(gh issue view "$NUM" --repo $REPO --json id --jq .id)
gh api graphql -f query='mutation($p:ID!,$c:ID!){ addSubIssue(input:{issueId:$p, subIssueId:$c}){ subIssue { number } } }' \
  -F p="$PARENT_ID" -F c="$CHILD_ID"
```

## Closing multiple work items from one PR

GitHub only auto-closes an issue when the PR body contains a **closing keyword + that issue's number**.
A single `Closes #1, #2` links only the first. Use **one keyword per issue, one per line**. Closing a
parent feature does **not** close its sub-issues, and closing every sub-issue does **not** close the
parent. List each work item the PR actually resolves. Generate the block with
`New-CohesionWorkItem.ps1 -EmitClosesBlock` from the same worktree.

## Re-discovering IDs if the schema changes

```bash
# Project node id
gh project view 13 --owner assimalign --format json --jq .id
# All single-select fields with their option ids
gh project field-list 13 --owner assimalign --format json \
  --jq '.fields[] | select(.type=="ProjectV2SingleSelectField") | {name, id, options:[.options[]|{name,id}]}'
```
