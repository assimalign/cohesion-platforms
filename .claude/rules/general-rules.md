---
paths:
  - "**/*.cs"
  - "**/*.csproj"
---

# General Rules

Required/forbidden C# patterns for this repo. The files in `.claude/rules/` are the canonical coding standard.

## Required patterns

### File-scoped namespaces
```csharp
namespace Assimalign.Cohesion.Database;

public class DatabaseEngine { }
```

### `CohesionProjectReference` for internal deps
```xml
<CohesionProjectReference Include="Assimalign.Cohesion.Core" />
```

### `CohesionPackageReference` for NuGet packages
```xml
<CohesionPackageReference Include="Newtonsoft.Json" />
```
Version goes in `build/Targets/Build.References.Packages.targets` first.

### Namespace matches assembly name exactly
- Assembly: `Assimalign.Cohesion.Database.Documents`
- Namespace: `namespace Assimalign.Cohesion.Database.Documents;`

### Target framework
- Libraries target `net10.0` — but the target framework is centrally managed via `TargetFrameworkLatest` in `build/Targets/Build.TargetFramework.props`, so per-project overrides are normally not needed.
- Sanctioned exception: MSBuild task projects (e.g., `build/Tasks/`) target `netstandard2.0` so they load in-process under both VS (Framework MSBuild) and `dotnet` (Core MSBuild); `analyzers/` projects, if ever added, do the same.

### Preview language features
```xml
<PropertyGroup>
  <LangVersion>Preview</LangVersion>
  <EnablePreviewFeatures>true</EnablePreviewFeatures>
</PropertyGroup>
```
These are also centrally managed. Don't duplicate per project unless the project genuinely needs to deviate.

### Markdown files use UPPERCASE
- ✅ `README.md`, `CONTRIBUTING.md`, `LICENSE`
- ❌ `readme.md`, `contributing.md`
- Exception: files whose names are fixed by external tooling keep their conventional casing (e.g., `.github/pull_request_template.md`, files under `.claude/**`).
- API reference needs no exception: **folders** under `docs/Assembly/` mirror CLR namespace/type names, and each type's page is `docs/Assembly/<Namespace>/<Type>/OVERVIEW.md`.

### Direct throws or .NET 10 extension type methods, not `ThrowHelper`
- Use direct `throw` statements or framework guard APIs (e.g., `ArgumentNullException.ThrowIfNull`) when the logic is local.
- If reusable throw behavior is needed, implement as a .NET 10 extension type method in `Extensions/`.

### `.NET 10 extension(...)` syntax for extension members
```csharp
public static class DatabaseExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddDatabase()
        {
            services.AddSingleton<IDatabase, Database>();
            return services;
        }
    }
}
```
The legacy `this T param` syntax is forbidden in new code.

### Scope exception roots to a library or service area
- Use local roots like `FileSystemException`, `HttpException`, `DatabaseException` when an area needs a shared base.
- Area-root exceptions inherit directly from `Exception` or `SystemException` unless there's a strong BCL reason otherwise.
- Keep exception inheritance local to the owning area.

## Forbidden patterns

### Block-scoped namespaces
```csharp
// ❌ WRONG
namespace Assimalign.Cohesion.Database
{
    public class DatabaseEngine { }
}
```

### Relative paths in project references
```xml
<!-- ❌ WRONG -->
<ProjectReference Include="..\..\Core\Assimalign.Cohesion.Core\src\Assimalign.Cohesion.Core.csproj" />
```

### Adding package references without centralized versions
- Always add to `build/Targets/Build.References.Packages.targets` first.
- Then use `CohesionPackageReference`.

### Raw `PackageReference`
```xml
<!-- ❌ WRONG -->
<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
```

### Any `Microsoft.Extensions.*` package
Standing architectural commitment. No exceptions without explicit user confirmation that they understand the impact.

### Public classes without XML documentation
```csharp
// ❌ WRONG
public interface IDatabase { }

// ✅ CORRECT
/// <summary>
/// Provides database access functionality.
/// </summary>
public interface IDatabase { }
```

### `ThrowHelper` / `ThrowHelpers` types
- Do not add helper classes whose primary purpose is throwing exceptions.
- When touching existing usages, migrate toward direct throws or extension type methods.

### Legacy `this` extension syntax in new code
```csharp
// ❌ WRONG
public static class DatabaseExtensions
{
    public static IServiceCollection AddDatabase(this IServiceCollection services)
    {
        return services;
    }
}
```

### Framework-wide base exception types for unrelated areas
- No `CohesionException`, `NetworkException`, or similar cross-framework roots.
- Unrelated libraries should not share exception ancestry just for convention.

## Naming conventions

### Types
| Kind | Convention | Example |
|---|---|---|
| Interface | `I` prefix | `IDatabase`, `IConfigurationProvider` |
| Class | PascalCase, noun | `DatabaseEngine`, `ConfigurationBuilder` |
| Exception | `Exception` suffix | `DatabaseConnectionException` |
| Extension container | `Extensions` suffix | `ServiceCollectionExtensions` |

### Members
| Kind | Convention | Example |
|---|---|---|
| Method | PascalCase, verb-first | `ExecuteQuery`, `GetAsync` |
| Property | PascalCase, noun | `ConnectionString`, `MaxRetries` |
| Private field | `_camelCase` | `_connectionString`, `_retryCount` |
| Public const | PascalCase | `DefaultTimeout` |
| Private const | camelCase | `maxRetries` |
| Parameter | camelCase | `connectionString`, `timeout` |
| Local variable | camelCase | `connectionString`, `retryCount` |

## Code organization

### Library folder structure
```
platforms/{Platform}/Assimalign.Cohesion.{Library}/
├── src/
│   ├── Abstractions/      # Interfaces only
│   ├── Extensions/        # Extension members
│   ├── Internal/          # Internal implementation
│   ├── Exceptions/        # Custom exceptions
│   ├── ValueObjects/      # Value types
│   └── [Feature folders]
├── docs/
│   ├── OVERVIEW.md
│   ├── DESIGN.md
│   └── Assembly/          # API reference by namespace and type
└── tests/
    ├── TestObjects/
    └── Shared/
```

### File organization rules
1. **One public type per file** (exceptions: nested types, related enums).
2. **File name matches primary type name** — e.g., `DatabaseEngine.cs` contains `class DatabaseEngine`.
3. **Variant families use grouped root-first naming:** `Http2Frame.Header.cs` and `Http2Frame.Ping.cs`, not `HeaderHttp2Frame.cs` and `PingHttp2Frame.cs`. The concrete type name remains variant-first; only the filename is grouped.
4. **Extension members** live in partial classes under `Extensions/` using `extension(...)`.
5. **Test files** named `{Feature}Tests.cs`.

### Using directives
**Order:**
1. `System.*` namespaces
2. Third-party namespaces
3. `Assimalign.Cohesion.*` namespaces
4. Blank line before code

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using NUlid;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Configuration;

namespace Assimalign.Cohesion.Database;
```

**Never use global usings or `<Using Include="..." />` items in project files. Add explicit `using` directives in each file.**

## Access modifiers

1. **Implementation classes:** `internal` by default.
   ```csharp
   internal class DatabaseConnectionPool { }
   ```
2. **Public APIs use interfaces.**
   ```csharp
   public interface IDatabase { }
   internal class Database : IDatabase { }
   ```
3. **Extension containers:** always `public static`, with members inside `extension(...)`.
4. **Nested types:** match outer type visibility unless explicitly different.
5. **Before introducing a new abstraction, check whether one already exists** in the same service root or shared library. Placeholder folders and placeholder projects are not final architecture boundaries — add projects when needed to preserve modularity and clean dependency flow.

## Interface-first with a guided abstract base

Public APIs stay interface-first — the interface is the contract consumers depend on. Where implementers benefit from guidance, also ship a **public `abstract` base class that explicitly implements the interface** and forwards each member to a strongly-typed `abstract`/`virtual` member:

```csharp
public interface IConnectionListener
{
    ValueTask<IConnection> AcceptAsync(CancellationToken cancellationToken = default);
}

public abstract class ConnectionListener : IConnectionListener
{
    // Richer concrete-typed member guides the implementer; declared public so
    // holders of the concrete type get the better signature without casting.
    public abstract ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default);

    async ValueTask<IConnection> IConnectionListener.AcceptAsync(CancellationToken cancellationToken)
        => await AcceptAsync(cancellationToken).ConfigureAwait(false);
}

internal sealed class TcpConnectionListener : ConnectionListener { /* ... */ }
```

The interface remains the canonical public surface; concrete types derive from the base and stay `internal` where possible. Use the explicit-implementation forwarding only where the base can offer a richer, concrete-typed member; members without a richer counterpart are declared `public`/`protected abstract` directly.

## Service composition

This repo implements the deployment/hosting plane for Cohesion's application model: platform gateways realize an `IApplicationModel` (from `Assimalign.Cohesion.ApplicationModel`) onto a concrete target (Kubernetes, Docker). Preserve the Cohesion layering model (L1 = foundation libraries and SDK/tooling, L2 = application runtime and composition, L3 = service platforms; see `docs/DELIVERY_ROADMAP.md` in the cohesion repo) and the gateway layering rules in `platform-areas.md` — gateways reference ApplicationModel contracts and `{Resource}.ApplicationModel` manifest packages only, never `{Resource}.Application` runtimes.

## Async / await

1. **Async methods end in `Async`.**
   ```csharp
   public async Task<string> GetDataAsync() { }
   ```
2. **Always accept `CancellationToken cancellationToken = default`.**
3. **Avoid `async void`** except for event handlers.
4. **Prefer `ValueTask<T>` for frequently-called async methods** where the result is often available synchronously (e.g., cache hits).

## Exception handling

1. **Catch specific exceptions, not bare `Exception`.**
2. **Use custom exceptions for domain errors**, scoped to the owning area.
3. **Preserve stack trace when rethrowing** — `throw;`, never `throw ex;`.
4. **Avoid `ThrowHelper` patterns** — direct throws or extension type methods.

## Performance

1. Prefer `ValueTask<T>` in hot async paths.
2. Use `Span<T>` and `Memory<T>` for buffer operations.
3. Avoid allocations in hot paths.

## AOT posture

**This repo does not carry cohesion's AOT mandate** (owner decision, 2026-07-20). Gateways are deploy-time control planes, not hot-path services — the cohesion repo's libraries (the deployed runtimes that need the performance) keep the hard `IsAotCompatible` requirement; this repo does not, and `KubernetesClient`-style dependencies need no exception machinery.

Keep the hygiene where it is free, because it also buys startup time and smaller closures:

- Prefer source-generated `System.Text.Json` serializers over reflection-based serialization
- No dynamic code generation at runtime, no `Assembly.LoadFrom()`
- Exception: any code in this repo that ships *inside* deployed workloads (e.g., the sample resource runtime used by E2E tests) models a cohesion service and follows the cohesion AOT posture (`IsAotCompatible=true`, AOT-publishable)

Reintroducing a repo-wide AOT mandate is an architectural decision requiring explicit owner confirmation.
