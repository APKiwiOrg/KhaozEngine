# KhaozEngine.App

Game-agnostic app identity / runtime helpers. Pure BCL, no MonoGame dependency.

`BuildMetadata.Read` reads `AssemblyMetadata` items (emitted by a project's `Directory.Build.props`)
back at runtime, so a game can surface its own version / build name / bundle id without re-deriving
them. The caller passes the assemblies to probe - the engine never guesses via
`GetExecutingAssembly` (that would resolve to the engine, not the game).

```csharp
using System.Reflection;
using KhaozEngine.App;

// Probe the game's own assembly, then the entry assembly, else fall back:
string version = BuildMetadata.Read(
    "MyGame.Version", "0.0.0",
    typeof(MyGameMarker).Assembly, Assembly.GetEntryAssembly());
```

First assembly with a matching, non-whitespace `AssemblyMetadata` value wins; null assemblies are
skipped; otherwise the fallback is returned.
