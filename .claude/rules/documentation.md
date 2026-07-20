---
paths:
  - "**/*.md"
  - "**/docs/**"
---

# Documentation

## Markdown file naming

Markdown files use UPPERCASE names (`README.md`, `OVERVIEW.md`, `DESIGN.md`). Exception: files whose names are fixed by external tooling keep their conventional casing (e.g., `.github/pull_request_template.md`, files under `.claude/**`). API reference under `docs/Assembly/` needs no exception — its **folders** mirror CLR namespace/type names, while every file is `OVERVIEW.md` (see "Assembly documentation layout" below).

## Three layers of documentation

Cohesion has three layers of documentation, each with distinct purpose and audience.

1. **Area-level `README.md`** — overview of a major area (e.g., `platforms/Kubernetes/`, `platforms/Docker/`)
2. **Project-level docs** — `OVERVIEW.md`, `DESIGN.md`, and `docs/Assembly/` per project
3. **XML doc comments** — on public APIs

Each project with `src/` and `tests/` folders should also have a sibling `docs/` folder.

## Project-level documentation

Required files per project:

- `docs/OVERVIEW.md` — project purpose, scope, dependencies, and usage at a high level
- `docs/DESIGN.md` — architecture, important design choices, lifecycle behavior, extension points, operational concerns, known constraints
- `docs/Assembly/` — API reference material organized by namespace and type

### `docs/DESIGN.md` — required for every library

Every library under `platforms/` must have a `docs/DESIGN.md`. The purpose is to make the *reasoning* behind the library's shape readable without re-deriving it from diffs, commit history, or code archaeology — when a future session surveys the code, `DESIGN.md` is what tells them why it looks the way it does.

**Canonical example:** `libraries/Dns/Assimalign.Cohesion.Dns/docs/DESIGN.md` in the cohesion repo (`C:\Source\repos\assimalign\cohesion`). Match its depth and structure for new libraries; once this repo has a mature `DESIGN.md` of its own, nominate it here as the local canonical example.

**Typical sections** (adapt to the library — these aren't a rigid template):

- **Design intent** — what the library is for at the architectural level, in one or two paragraphs.
- **Why-this-not-that decisions** — each major design choice (e.g., abstract class vs interface, sealed hierarchy vs plugin model, single root exception vs family) with the rationale and the trade-off accepted. The "contrast with X" framing in the Dns DESIGN.md (interfaces for FileSystem, abstract classes for DNS) is the model — name the alternative you rejected and why.
- **Family map** — if the library is part of a multi-package family, list the packages, their roles, and the one-way dependency direction.
- **Lifecycle pattern** — how disposal, cancellation, and resource ownership work, with the base-class shape if applicable.
- **Error model** — exception root, error codes, wire/transport mappings, who throws what.
- **Wire/protocol scope** — for protocol libraries, what's parsed/serialized, what's intentionally not.
- **AOT posture** — what the library deliberately avoids to stay AOT-clean.
- **Adding to the family / extending** — concrete steps for the next library in the family or the next extension point.
- **Non-goals** — what the library deliberately won't do, and why. This is high-value because it heads off future "should we add X?" questions.

### Keeping `DESIGN.md` current

When a code change alters or extends a design decision, `DESIGN.md` is updated in the same commit. Examples of what triggers an update:

- Lifecycle pattern changes (new `DisposeAsyncCore` hook, new ownership rule)
- Error model changes (new error code, changed mapping, new exception root)
- Contract shape changes (interface → abstract base, new family member, new extension point)
- AOT posture relaxations or tightenings
- Non-goal becomes a goal, or vice versa
- A new "why-this-not-that" decision is made (record it before you forget the alternative you rejected)

A `DESIGN.md` that lags the code actively misleads — worse than not having one. If you're not sure whether a change rises to the level of a `DESIGN.md` update, ask: *"would the existing DESIGN.md surprise or mislead someone reading it after this change?"* If yes, update it.

### Assembly documentation layout

- Namespace folders under `docs/Assembly/` mirror the documented namespace (e.g., `docs/Assembly/System.IO/`)
- Each documented type gets a **folder** named for the type, containing an `OVERVIEW.md` (e.g., `docs/Assembly/System.IO/Glob/OVERVIEW.md`). The folder leaves room for additional per-member or design pages beside the overview later.
- A namespace folder may also carry its own `OVERVIEW.md` introducing the assembly/namespace (the IdentityModel family uses this while its per-type reference is pending).
- Folder names mirror CLR namespace and type names exactly; the markdown files themselves stay UPPERCASE (`OVERVIEW.md`), so API reference needs no naming exception.
- API reference docs should outline: public surface area, constructor or factory behavior, methods, properties, exceptions, usage notes

## Area-level `README.md`

Each major area root contains a `README.md` providing an overview. Examples:
- `platforms/Kubernetes/README.md`
- `platforms/Docker/README.md`

Area `README.md` files should summarize:
- The purpose of the area
- The major projects or services it contains
- How the area fits into the Cohesion layering model (this repo realizes the L2 application model onto deployment targets)
- Important dependencies on other areas and on cohesion-repo packages
- Links to project-level `OVERVIEW.md` and `DESIGN.md` files where relevant

## XML documentation requirements

**Public APIs MUST have:**
- `<summary>` — brief description
- `<param>` — for each parameter
- `<returns>` — for non-void methods
- `<exception>` — for thrown exceptions
- `<remarks>` — for additional details (optional but recommended)

**Example:**
```csharp
/// <summary>
/// Executes a database query asynchronously.
/// </summary>
/// <param name="query">The SQL query to execute.</param>
/// <param name="parameters">Query parameters to bind.</param>
/// <param name="cancellationToken">Cancellation token for the operation.</param>
/// <returns>A task representing the query result.</returns>
/// <exception cref="DatabaseConnectionException">Thrown when connection fails.</exception>
/// <remarks>
/// This method automatically retries on transient failures up to 3 times.
/// </remarks>
public async Task<QueryResult> ExecuteAsync(
    string query,
    Dictionary<string, object> parameters,
    CancellationToken cancellationToken = default)
{
    // Implementation
}
```

**Internal types MAY omit XML docs** but should use code comments for complex logic.

## When XML docs drift

Common drift modes worth checking before completion:
- A new public method added without `<summary>`
- A `CancellationToken` parameter added without a `<param>` entry
- A new exception thrown from a method without a matching `<exception>`
- A `<returns>` left over from a refactor that now describes the wrong shape

Commit-message and branch-naming conventions live in `workflow.md` (always loaded).
