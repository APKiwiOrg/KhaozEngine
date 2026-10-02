using System.Text.Json.Serialization;

namespace KhaozEngine.Ecs;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the <see cref="WorldSerializer"/> save envelope
/// (<c>SaveDoc</c>, <c>EntityDoc</c>, <c>FreeSlot</c> and ordered component-key lists). Component values are raw
/// <c>JsonElement</c>, so
/// the envelope carries no game types and is fully reflection-free - it keeps the save/load envelope NativeAOT-safe.
/// Default options keep the established compact PascalCase member encoding. Metadata generation covers the complete
/// envelope object graph, including empty archetype signatures.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WorldSerializer.SaveDoc))]
internal sealed partial class WorldSaveJsonContext : JsonSerializerContext
{
}
