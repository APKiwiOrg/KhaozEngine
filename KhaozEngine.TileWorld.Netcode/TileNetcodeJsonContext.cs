using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the tile persistence DTOs. The generation options
/// reproduce <c>NetWorldJsonContext</c>'s exactly: indented write with a pinned <c>"\n"</c> newline (these blobs
/// are content-hashed and compared across operating systems, so the bytes must be canonical LF everywhere),
/// case-insensitive names, skipped comments and allowed trailing commas on read. Metadata generation mode, not
/// the serialization fast path, so a null <c>byte[]</c> still encodes as <c>null</c> rather than <c>""</c>.
/// <para>Source-generated rather than reflection-based so these records round-trip under NativeAOT, which is what
/// a headless server is published as.</para>
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = true,
    NewLine = "\n",
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(TilePlayerRecord))]
internal sealed partial class TileNetcodeJsonContext : JsonSerializerContext
{
}
