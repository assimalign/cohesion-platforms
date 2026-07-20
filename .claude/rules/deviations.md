---
paths:
  - "**/*.cs"
  - "**/*.csproj"
  - "**/*.props"
  - "**/*.targets"
---

# Handling User-Directed Exceptions to the Rules

The user may ask for an approach that contradicts one of the repo coding rules (`.claude/rules/`) — for example, abstract classes instead of the interface-first pattern. When you detect such a conflict:

1. **Name the rule explicitly.** Quote or paraphrase the specific rule the request deviates from. Do not silently comply — the user may have forgotten the rule exists, and surfacing it is part of the value.
2. **Confirm intent before proceeding**, unless the user's message already explicitly acknowledges the deviation (e.g., "I know this breaks the interface-first rule, but..."). A one-line check is enough.
3. **Scope the exception narrowly.** The deviation applies only to the specific component, library, or area the user named. The next component in the same session still follows the original rule. Do not generalize.
4. **Document the deviation in code** at the relevant entry point so future readers and future sessions understand it is intentional and don't "correct" it back:

   ```csharp
   // Deviates from the repo <rule name> rule per design decision: <one-line rationale>.
   ```

   ```xml
   <!-- Deviates from the repo <rule name> rule per design decision: <one-line rationale>. -->
   ```

5. **Surface the deviation in the change summary** so it is traceable in commit messages and PR descriptions.

## Rules requiring especially explicit confirmation

These are architectural commitments, not stylistic preferences — deviating has cascading consequences. Restate the architectural reason the rule exists and ask the user to confirm they understand the trade-off before proceeding:

- Reintroducing a repo-wide AOT mandate (this repo deliberately does **not** carry cohesion's `IsAotCompatible` requirement — see `general-rules.md § AOT posture`)
- No `Microsoft.Extensions.*` references
- The `$(CohesionVersion)` single-source-of-truth chain
- Never hardcoding a version on `<Import Sdk>`
- The gateway layering rule: gateways reference ApplicationModel contracts and `{Resource}.ApplicationModel` manifest packages only, never `{Resource}.Application` runtimes
- Digest-pinned container image references — deployments always pull by `@sha256:` digest, never by tag
