# KhaozEngine.Serialization

Shared `System.Text.Json` option baselines so every KhaozEngine package serializes JSON consistently.

```csharp
using KhaozEngine.Serialization;

// Loading tolerant config (case-insensitive, // comments, trailing commas):
var cfg = JsonSerializer.Deserialize<MyConfig>(json, JsonDefaults.TolerantRead);

// Writing a human-readable save/settings file (indented):
File.WriteAllText(path, JsonSerializer.Serialize(state, JsonDefaults.IndentedWrite));

// Round-tripping structs that expose fields (e.g. ECS components):
JsonSerializer.Serialize(component, JsonDefaults.IncludeFields);
```

Each property returns a single shared instance. `System.Text.Json` freezes options on first use, so
treat them as read-only. If you need converters or other tweaks, build your own `JsonSerializerOptions`
and pass it through the relevant API instead.

Pure BCL, no MonoGame dependency.
