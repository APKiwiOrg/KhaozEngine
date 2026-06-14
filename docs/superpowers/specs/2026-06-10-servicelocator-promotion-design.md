# ServiceLocator promotion (Batch 1, item 4)

Status: approved design, pre-implementation
Date: 2026-06-10

## Goal

Lift Nullwake's generic `ServiceLocator` into the shared `KhaozEngine.App` package so all games
(and the engine's `ScreenManager.Services` slot) can use one maintained service registry instead
of threading dependencies through constructors.

Source: `Nullwake.Core/Engine/ServiceLocator.cs` - already fully generic, zero game coupling,
implements `IServiceProvider`. Engine has no equivalent yet.

## Decisions (from brainstorming)

1. **Lives in `KhaozEngine.App`** (the pure-BCL package from items 3/5). A service registry is a
   runtime/composition concern and is pure BCL, so it does not belong in the MonoGame-bound
   `KhaozEngine.Screens` even though it pairs with `ScreenManager.Services`.
2. **Lift-and-shift**, namespace `Nullwake.Core.Engine` → `KhaozEngine.App`. Public API unchanged.
3. **Backing store: `ConcurrentDictionary<Type, object>`** (the only change from the original
   `Dictionary`). `Replace` implies possible runtime mutation while other threads resolve, so the
   shared version is safe under concurrent register/replace/resolve. No public API or behaviour
   change. Matches the package posture (FileLogger locks; AppDataPaths is thread-safe).

## Public API

Namespace `KhaozEngine.App`:

```csharp
public sealed class ServiceLocator : IServiceProvider
{
    public void Register<T>(T service) where T : class;   // throws InvalidOperationException if T already registered
    public void Replace<T>(T service) where T : class;    // adds or overwrites
    public T Get<T>() where T : class;                     // throws InvalidOperationException if not registered
    public T? TryGet<T>() where T : class;                 // null if not registered
    public bool Has<T>() where T : class;
    public object? GetService(Type serviceType);           // IServiceProvider; null if not registered
}
```

## Behaviour contract

Preserved verbatim from the original; backing store is `ConcurrentDictionary<Type, object>`:

- `Register<T>(service)`: `ArgumentNullException.ThrowIfNull(service)`; `TryAdd(typeof(T), service)`;
  if it returns false (already present) throw `InvalidOperationException` ("Service of type {Name}
  is already registered.").
- `Replace<T>(service)`: `ArgumentNullException.ThrowIfNull(service)`; `_services[typeof(T)] = service`
  (atomic upsert).
- `Get<T>()`: `TryGetValue(typeof(T))` → cast to `T`; if absent throw `InvalidOperationException`
  ("Service of type {Name} is not registered.").
- `TryGet<T>()`: `TryGetValue` → cast to `T`, or `null`.
- `Has<T>()`: `ContainsKey(typeof(T))`.
- `GetService(Type serviceType)`: `TryGetValue(serviceType)` → instance or `null` (the
  `IServiceProvider` contract; never throws).

Keys are the compile-time `typeof(T)` for the generic methods (registration under the interface
type the caller specifies, exactly as today).

## Project / packaging changes

- Add `KhaozEngine.App/ServiceLocator.cs` to the existing `KhaozEngine.App` project.
- No csproj change (pure BCL; `System.Collections.Concurrent` needs no package on net10.0).
- No slnx / Tests-csproj wiring changes (package + reference already exist).
- Inherits the shared `<Version>` from `Directory.Build.props`.

## Testing (headless, KhaozEngine.Tests)

Pure BCL; define a couple of tiny marker interfaces + implementations in the test file
(e.g. `IFoo`/`Foo`, `IBar`/`Bar`).

- `Register<IFoo>(foo)` then `Get<IFoo>()` returns `foo`; `GetService(typeof(IFoo))` returns `foo`;
  the same via an `IServiceProvider` reference returns `foo`.
- `Register<IFoo>` twice → `InvalidOperationException`.
- `Get<IFoo>()` when unregistered → `InvalidOperationException`.
- `TryGet<IFoo>()` unregistered → `null`; registered → the instance.
- `Has<IFoo>()` → false before, true after registration.
- `Replace<IFoo>(foo2)` after `Register<IFoo>(foo1)` → `Get<IFoo>()` returns `foo2`; `Replace<IBar>(bar)`
  with nothing registered → adds it (`Get<IBar>()` returns `bar`).
- `GetService(typeof(IFoo))` when unregistered → `null` (does not throw).
- `Register<IFoo>(null!)` and `Replace<IFoo>(null!)` → `ArgumentNullException`.

## Release handling

Item 4 of Batch 1. No `<Version>` bump, no `CHANGELOG.md`, no `dotnet pack` here - deferred to the
single end-of-batch `3.0.0 → 3.1.0` release.

## Out of scope

- Migrating consumers (adopt PRs, after release): Nullwake re-points its `ServiceLocator` usages at
  the KE type and deletes its copy; Hardpoint/SpaceGame can adopt it to replace constructor-threaded
  services if/when they choose. Wiring `ScreenManager.Services = serviceLocator` is per-game.
