---
paths:
  - "**/tests/**"
---

# Testing

## Test naming

**Test class:** `{Feature}Tests`
```csharp
public class DatabaseConnectionTests { }
```

**Test method:** `{Method}_{Scenario}_{ExpectedBehavior}`, with the display name set through the `Fact`/`Theory` attribute
```csharp
[Fact(DisplayName = "Cohesion Test [Kubernetes] - Reconcile: Should apply digest-pinned deployment")]
public async Task Reconcile_OnExecutableEndpointResource_ShouldApplyDigestPinnedDeployment()
{
    // Test implementation
}
```

## Test structure — AAA pattern

```csharp
[Fact]
public void Cache_OnMiss_ShouldReturnNull()
{
    // Arrange
    var cache = new MemoryCache();
    
    // Act
    var result = cache.Get("nonexistent");
    
    // Assert
    result.ShouldBeNull();
}
```

## Assertions — Shouldly

Shouldly is the single assertion library for the repo.

```csharp
// ✅ Shouldly
result.ShouldNotBeNull();
result.Count.ShouldBe(5);
result.ShouldContain(x => x.Id == "123");

// ❌ FluentAssertions — forbidden (v8+ moved to a paid commercial license; migrated out 2026-07)
result.Should().NotBeNull();

// ❌ Avoid traditional Assert
Assert.NotNull(result);
Assert.Equal(5, result.Count);
```

Caveat: Shouldly's string `ShouldContain`/`ShouldNotContain` default to case-INSENSITIVE comparison. Pass `Case.Sensitive` when asserting exact fragments (e.g., serialized JSON).

## Test folder layout

```
platforms/{Platform}/Assimalign.Cohesion.{Library}/tests/
├── TestObjects/    # Test fixtures and supporting types
├── Shared/         # Shared test code across multiple test files
└── {Feature}Tests.cs
```

## Coverage expectations

- Add or update tests for behavior changes.
- Spec-driven services (protocol implementations, RFC-backed features) need unit tests **plus** compliance or interoperability tests.
- This repo has no hard AOT mandate (see `general-rules.md § AOT posture`); still avoid gratuitous reflection in test infrastructure, and keep sample/workload runtimes AOT-publishable.

## Cancellation in async tests

Async test methods should still exercise `CancellationToken` where the system under test takes one. The repo currently pins xUnit v2 (no ambient `TestContext`), so create a token in the test — e.g. a `CancellationTokenSource` with a timeout — or pass `CancellationToken.None` when cancellation is not the behavior under test.
