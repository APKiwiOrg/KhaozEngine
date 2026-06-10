# ServiceLocator Promotion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Lift Nullwake's generic `ServiceLocator` into `KhaozEngine.App`, swapping its backing `Dictionary` for a `ConcurrentDictionary` (no public API change).

**Architecture:** One `public sealed class ServiceLocator : IServiceProvider` in `KhaozEngine.App`, backed by `ConcurrentDictionary<Type, object>`. Pure BCL. Headless xUnit tests drive every method via tiny marker interfaces.

**Tech Stack:** C# / net10.0, `System.Collections.Concurrent`, xUnit. Package `KhaozEngine.App` already exists (Batch 1 items 3/5).

**Spec:** `docs/superpowers/specs/2026-06-10-servicelocator-promotion-design.md`

---

## File Structure

- `KhaozEngine.App/ServiceLocator.cs` — the promoted class (sole responsibility: register/resolve services by type).
- `KhaozEngine.Tests/ServiceLocatorTests.cs` — tests + tiny marker interfaces/impls.

No new package, no csproj/slnx/Tests-wiring changes (all exist). No version bump / CHANGELOG / pack — deferred to the single end-of-batch 3.1.0 release.

All commands run from the worktree root: `/Users/antonio/KhaozEngine/.claude/worktrees/batch1-promote`.

---

## Task 1: ServiceLocator (TDD)

**Files:**
- Create: `KhaozEngine.Tests/ServiceLocatorTests.cs`
- Create: `KhaozEngine.App/ServiceLocator.cs`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/ServiceLocatorTests.cs`:

```csharp
using System;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests;

public class ServiceLocatorTests
{
    private interface IFoo { }
    private sealed class Foo : IFoo { }
    private interface IBar { }
    private sealed class Bar : IBar { }

    [Fact]
    public void Register_ThenGet_ReturnsSameInstance()
    {
        var locator = new ServiceLocator();
        var foo = new Foo();

        locator.Register<IFoo>(foo);

        Assert.Same(foo, locator.Get<IFoo>());
    }

    [Fact]
    public void GetService_ByRuntimeType_ReturnsInstance()
    {
        var locator = new ServiceLocator();
        var foo = new Foo();
        locator.Register<IFoo>(foo);

        // Directly and via the IServiceProvider contract.
        Assert.Same(foo, locator.GetService(typeof(IFoo)));
        IServiceProvider provider = locator;
        Assert.Same(foo, provider.GetService(typeof(IFoo)));
    }

    [Fact]
    public void Register_Duplicate_Throws()
    {
        var locator = new ServiceLocator();
        locator.Register<IFoo>(new Foo());

        Assert.Throws<InvalidOperationException>(() => locator.Register<IFoo>(new Foo()));
    }

    [Fact]
    public void Get_Unregistered_Throws()
    {
        var locator = new ServiceLocator();

        Assert.Throws<InvalidOperationException>(() => locator.Get<IFoo>());
    }

    [Fact]
    public void TryGet_ReturnsInstanceOrNull()
    {
        var locator = new ServiceLocator();
        var foo = new Foo();

        Assert.Null(locator.TryGet<IFoo>());
        locator.Register<IFoo>(foo);
        Assert.Same(foo, locator.TryGet<IFoo>());
    }

    [Fact]
    public void Has_ReflectsRegistration()
    {
        var locator = new ServiceLocator();

        Assert.False(locator.Has<IFoo>());
        locator.Register<IFoo>(new Foo());
        Assert.True(locator.Has<IFoo>());
    }

    [Fact]
    public void Replace_OverwritesExisting()
    {
        var locator = new ServiceLocator();
        var first = new Foo();
        var second = new Foo();
        locator.Register<IFoo>(first);

        locator.Replace<IFoo>(second);

        Assert.Same(second, locator.Get<IFoo>());
    }

    [Fact]
    public void Replace_AddsWhenAbsent()
    {
        var locator = new ServiceLocator();
        var bar = new Bar();

        locator.Replace<IBar>(bar);

        Assert.Same(bar, locator.Get<IBar>());
    }

    [Fact]
    public void GetService_Unregistered_ReturnsNull()
    {
        var locator = new ServiceLocator();

        Assert.Null(locator.GetService(typeof(IFoo)));
    }

    [Fact]
    public void Register_Null_Throws()
    {
        var locator = new ServiceLocator();

        Assert.Throws<ArgumentNullException>(() => locator.Register<IFoo>(null!));
    }

    [Fact]
    public void Replace_Null_Throws()
    {
        var locator = new ServiceLocator();

        Assert.Throws<ArgumentNullException>(() => locator.Replace<IFoo>(null!));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ServiceLocatorTests" -v q`
Expected: FAIL — compile error, `ServiceLocator` does not exist in namespace `KhaozEngine.App`.

- [ ] **Step 3: Write the implementation**

Create `KhaozEngine.App/ServiceLocator.cs`:

```csharp
using System;
using System.Collections.Concurrent;

namespace KhaozEngine.App;

/// <summary>
/// Lightweight service locator for registering and resolving game systems by interface type.
/// Prefer this over tight coupling between systems. Implements <see cref="IServiceProvider"/> so it
/// can be stashed in the KhaozEngine ScreenManager's <c>Services</c> slot and cast back by screens.
/// Backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>, so register / replace / resolve are
/// safe under concurrent access.
/// </summary>
public sealed class ServiceLocator : IServiceProvider
{
    private readonly ConcurrentDictionary<Type, object> services = new();

    /// <summary>
    /// Registers a service instance under the given interface type.
    /// </summary>
    /// <typeparam name="T">The interface type to register under.</typeparam>
    /// <param name="service">The service instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A service of type <typeparamref name="T"/> is already registered.</exception>
    public void Register<T>(T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        Type type = typeof(T);

        if (!services.TryAdd(type, service))
        {
            throw new InvalidOperationException($"Service of type {type.Name} is already registered.");
        }
    }

    /// <summary>
    /// Replaces an existing service registration or adds a new one.
    /// </summary>
    /// <typeparam name="T">The interface type to register under.</typeparam>
    /// <param name="service">The service instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is null.</exception>
    public void Replace<T>(T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        services[typeof(T)] = service;
    }

    /// <summary>
    /// Resolves a registered service by interface type.
    /// </summary>
    /// <typeparam name="T">The interface type to resolve.</typeparam>
    /// <returns>The registered service instance.</returns>
    /// <exception cref="InvalidOperationException">No service of type <typeparamref name="T"/> is registered.</exception>
    public T Get<T>() where T : class
    {
        Type type = typeof(T);

        if (services.TryGetValue(type, out object? service))
        {
            return (T)service;
        }

        throw new InvalidOperationException($"Service of type {type.Name} is not registered.");
    }

    /// <summary>
    /// Attempts to resolve a registered service. Returns null if not found.
    /// </summary>
    /// <typeparam name="T">The interface type to resolve.</typeparam>
    /// <returns>The service instance, or null if not registered.</returns>
    public T? TryGet<T>() where T : class
    {
        return services.TryGetValue(typeof(T), out object? service) ? (T)service : null;
    }

    /// <summary>
    /// Returns true if a service of the given type is registered.
    /// </summary>
    /// <typeparam name="T">The interface type to check.</typeparam>
    public bool Has<T>() where T : class
    {
        return services.ContainsKey(typeof(T));
    }

    /// <summary>
    /// <see cref="IServiceProvider"/> implementation: resolves a registered service by runtime
    /// type, or null if not registered. Never throws.
    /// </summary>
    /// <param name="serviceType">The service type to resolve.</param>
    /// <returns>The registered service instance, or null.</returns>
    public object? GetService(Type serviceType)
    {
        return services.TryGetValue(serviceType, out object? service) ? service : null;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ServiceLocatorTests" -v q`
Expected: PASS — 11 passed.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.App/ServiceLocator.cs KhaozEngine.Tests/ServiceLocatorTests.cs
git commit -m "Add KhaozEngine.App.ServiceLocator"
```

---

## Task 2: Full suite green + isolated build

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -v q`
Expected: PASS — baseline (202) + 11 new = 213, 0 failed. (Confirm the baseline at the start; the delta is +11.)

- [ ] **Step 2: Build the package project in isolation (confirm no stray deps)**

Run: `dotnet build KhaozEngine.App/KhaozEngine.App.csproj -v q`
Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`. Confirms the package stays pure-BCL.

No commit needed (verification only).

---

## Notes for the release / adopt phase (do NOT do here)

- End-of-batch: bump `<Version>` 3.0.0 → 3.1.0, one `CHANGELOG.md` entry for the batch, update `docs/CONSUMERS.md`, `dotnet pack -c Release -o ./local-feed`.
- Adopt: Nullwake re-points its `ServiceLocator` references at `KhaozEngine.App.ServiceLocator` and deletes its copy; wiring `ScreenManager.Services = serviceLocator` stays per-game. Hardpoint/SpaceGame can adopt to replace constructor-threaded services if they choose.
