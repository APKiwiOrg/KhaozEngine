# KhaozEngine.Content

Game-agnostic content tooling for KhaozEngine games. Load typed config from embedded-resource or disk
JSON (`ConfigLoader.Load<T>`), and validate JSON against JSON Schema (`JsonSchemaValidator`). Ships a
bundled `KhaozEngine.Content.Validator` tool and a `buildTransitive` target that validates a consumer's
`Data/` directory against its schemas at build time when `KhaozContentDataDir` is set. Pure .NET with
`JsonSchema.Net` schema validation.

The validator is bundled inside this package and is not a separate consumer package.

## Schema findings

`JsonSchemaValidator.Validate(instanceJson, schemaJson)` returns `ValidationReport`. Its positional
`ValidationReport(bool IsValid, IReadOnlyList<string> Errors)` constructor and developer-facing `Errors`
strings remain available. Inspect `Findings` for structured failures. It is empty by default, on successful
validation and when the schema cannot be parsed. Malformed instance JSON still throws `JsonException`.

Each immutable, engine-owned `JsonSchemaFinding` carries `InstanceLocation` as a JSON Pointer (empty for the
root), the failing `Keyword` when known, and the decoded `PropertyName` when that location addresses an object
property. Array elements and the root have no property name. A missing required property is not inferred from
error prose, so its finding addresses the containing object. For an unknown property refused by
`additionalProperties`, the location includes that property and the keyword is `additionalProperties`.
No schema-library evaluation objects escape this API.

Setting `ValidationReport.Findings` copies the collection into an owned read-only snapshot. Neither changing
the supplied collection nor casting the exposed collection can change the report's findings.
