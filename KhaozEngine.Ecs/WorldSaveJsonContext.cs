using System.Text.Json.Serialization;

namespace KhaozEngine.Ecs;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the <see cref="WorldSerializer"/> save envelope
/// (<c>SaveDoc</c> / <c>EntityDoc</c> / <c>FreeSlot</c>). Component values are embedded as raw <c>JsonElement</c>, so
/// the envelope carries no game types and is fully reflection-free - it keeps the save/load envelope NativeAOT-safe.
/// Default options (no indentation, PascalCase) match the historical envelope encoding byte-for-byte. Metadata
/// generation mode routes through the same converters as the reflection serializer, so the encoding stays identical.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WorldSerializer.SaveDoc))]
internal sealed partial class WorldSaveJsonContext : JsonSerializerContext
{
}
