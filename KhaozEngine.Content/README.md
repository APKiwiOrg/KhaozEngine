# KhaozEngine.Content

Game-agnostic content tooling for KhaozEngine games. Load typed config from embedded-resource or disk
JSON (`ConfigLoader.Load<T>`), and validate JSON against JSON Schema (`JsonSchemaValidator`). Ships a
bundled validator + a `buildTransitive` target that validates a consumer's `Data/` directory against its
schemas at build time when `KhaozContentDataDir` is set. Pure .NET (no MonoGame); depends on
`JsonSchema.Net`.
