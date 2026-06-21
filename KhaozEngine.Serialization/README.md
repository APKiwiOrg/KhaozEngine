# KhaozEngine.Serialization

Shared `System.Text.Json` option baselines so every KhaozEngine package serializes JSON consistently.

## JSONC is the engine standard for hand-authored JSON

JSONC (JSON with `//` and `/* */` comments and trailing commas) is the default for every hand-authored config,
manifest, settings file, and save the engine reads. The canonical read policy is the `Jsonc` class - the single
source of truth all engine JSON loads route through, so authors can comment and annotate their files.

```csharp
using KhaozEngine.Serialization;

// The JSONC read policy, one entry point per System.Text.Json reader:
MyConfig? cfg = Jsonc.Deserialize<MyConfig>(json);        // JsonSerializer
MyConfig? onDisk = Jsonc.DeserializeFile<MyConfig>(path); // read file + deserialize
using JsonDocument doc = Jsonc.ParseDocument(json);       // JsonDocument
JsonNode? node = Jsonc.ParseNode(json);                   // JsonNode

// Or pass the options to any API yourself:
JsonSerializer.Deserialize<MyConfig>(json, Jsonc.Options);          // JsonSerializerOptions
JsonDocument.Parse(json, Jsonc.DocumentOptions);                    // JsonDocumentOptions
JsonNode.Parse(json, Jsonc.NodeOptions, Jsonc.DocumentOptions);     // JsonNodeOptions
```

`JsonDefaults.TolerantRead` is the same instance as `Jsonc.Options`, kept under its historical name.

### Write side: plain JSON, by design

JSONC is a **read-time** convenience. `System.Text.Json` cannot emit comments, so the engine never writes JSONC:

- **Generated files** (settings, saves) are written as plain, indented JSON via `JsonDefaults.IndentedWrite`. A
  comment a user adds to a generated save survives only until the engine rewrites that file.
- **Hand-authored files** (config, content manifests) keep their comments because the engine only reads them.
- **Signed / wire formats** stay strict JSON on purpose: the `KhaozEngine.Updates` manifest is signed over its
  exact bytes and the apply-update config is source-generated for AOT, so neither uses the JSONC policy.

```csharp
// Writing a human-readable save/settings file (indented, plain JSON):
File.WriteAllText(path, JsonSerializer.Serialize(state, JsonDefaults.IndentedWrite));

// Round-tripping structs that expose fields (e.g. ECS components):
JsonSerializer.Serialize(component, JsonDefaults.IncludeFields);
```

Each property returns a single shared instance. `System.Text.Json` freezes options on first use, so
treat them as read-only. If you need converters or other tweaks, build your own `JsonSerializerOptions`
and pass it through the relevant API instead.

Pure BCL, no MonoGame dependency.
