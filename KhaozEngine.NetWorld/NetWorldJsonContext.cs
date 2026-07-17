using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the durable NetWorld persistence DTOs
/// (<see cref="PlayerRecord"/>, <see cref="WorldMetaRecord"/>, and the ban DTO). Replaces reflection-based
/// <c>System.Text.Json</c> so these blob-store records round-trip under NativeAOT.
///
/// <para>The generation options reproduce the historical encoding exactly - <c>WriteIndented</c> matches
/// <c>JsonDefaults.IndentedWrite</c> (the write side), and case-insensitive names + skipped comments + trailing
/// commas match <c>JsonDefaults.TolerantRead</c> / <c>Jsonc.Options</c> (the read side). One options instance carries
/// both, so encode and decode stay byte-for-byte compatible with records already in the wild via IWorldStore.
/// <c>NewLine</c> is pinned to <c>"\n"</c> (matching <c>JsonDefaults.IndentedWrite</c>): the default indented
/// writer emits the OS newline, so a Windows host would otherwise persist CRLF and produce a different blob than
/// a Linux host for the same record - these records are content-hashed and compared across OSes, so the bytes must
/// be canonical LF everywhere.</para>
///
/// <para>Metadata generation mode (not the serialization fast path) is deliberate: it routes through the same
/// converters as the reflection serializer, so a null <c>byte[]</c> (<see cref="PlayerRecord.Game"/>) still encodes as
/// <c>null</c> - the fast path writes it as an empty string <c>""</c>, which would break byte-for-byte compatibility.
/// It stays fully NativeAOT-safe (source-generated metadata, no reflection).</para>
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = true,
    NewLine = "\n",
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(PlayerRecord))]
[JsonSerializable(typeof(WorldMetaRecord))]
[JsonSerializable(typeof(WorldStoreBanStore.BanDto))]
internal sealed partial class NetWorldJsonContext : JsonSerializerContext
{
}
