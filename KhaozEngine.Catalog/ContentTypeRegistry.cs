using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace KhaozEngine.Catalog;

/// <summary>
/// One registered content type: everything <see cref="ContentTypeRegistry.RegisterContentType"/> was handed,
/// held immutably so a publish, a boot and a console all read the same declaration.
/// </summary>
public sealed class ContentTypeRegistration
{
    internal ContentTypeRegistration(
        ContentRegistrationBand band,
        ContentTypeId type,
        string typeKey,
        IContentRowCodec codec,
        IContentValidator? validator,
        ContentFieldSchema schema,
        ContentVisibility defaultVisibility,
        int chunkSlots,
        int maxRowBytes,
        int? maxDefinitionId,
        IContentLoadIndex? loadIndex)
    {
        Band = band;
        Type = type;
        TypeKey = typeKey;
        Codec = codec;
        Validator = validator;
        Schema = schema;
        DefaultVisibility = defaultVisibility;
        ChunkSlots = chunkSlots;
        MaxRowBytes = maxRowBytes;
        MaxDefinitionId = maxDefinitionId;
        LoadIndex = loadIndex;
    }

    /// <summary>The band the registering caller claimed, and the range its type id was checked against.</summary>
    public ContentRegistrationBand Band { get; }

    /// <summary>The stable numeric type id.</summary>
    public ContentTypeId Type { get; }

    /// <summary>The stable string type key, unique across the WHOLE registry rather than per band.</summary>
    public string TypeKey { get; }

    /// <summary>The row codec, the only path between a row and its canonical bytes.</summary>
    public IContentRowCodec Codec { get; }

    /// <summary>The type's own validator, which runs after the engine's own. Null when it declares none.</summary>
    public IContentValidator? Validator { get; }

    /// <summary>The ordered field list a row's values are parallel to.</summary>
    public ContentFieldSchema Schema { get; }

    /// <summary>The visibility a field inherits when it declares none.</summary>
    public ContentVisibility DefaultVisibility { get; }

    /// <summary>The id SLOTS per chunk, so a chunk boundary is <c>floor(id / ChunkSlots)</c>.</summary>
    public int ChunkSlots { get; }

    /// <summary>This type's own row cap, which <c>KEC0026</c> checks a row against.</summary>
    public int MaxRowBytes { get; }

    /// <summary>
    /// The per-type id CEILING, or null when the type's only ceiling is the 31 bits of positive
    /// <c>int</c> space. A type declares one when a FORMAT it is carried in cannot hold a bigger number.
    /// </summary>
    public int? MaxDefinitionId { get; }

    /// <summary>The derived table this type builds at load, or null when it derives nothing.</summary>
    public IContentLoadIndex? LoadIndex { get; }
}

/// <summary>
/// The content type registry of contracts 4.2: registration once at process start, the freeze at first pack
/// load, and lookup by type id or type key.
/// <para>
/// It is a map keyed by TYPE ID and it is NOT a static. Per instance, deliberately, which is why nothing in
/// this package needs a <c>DisableParallelization</c> collection (spec 2.6), and which is a departure from
/// the process-global ambients the consumer surveys found.
/// </para>
/// <para>
/// NOTHING anywhere derives an ordinal from registration order (contracts 4.3). Two processes that register
/// the same types in different orders produce byte-identical packs and byte-identical manifests, so
/// <see cref="ByTypeId"/> is sorted ascending always rather than being an insertion list.
/// </para>
/// </summary>
public sealed class ContentTypeRegistry
{
    /// <summary>The smallest legal chunk slot count, contracts 4.5.</summary>
    public const int MinChunkSlots = 256;

    /// <summary>The largest legal chunk slot count, contracts 4.5.</summary>
    public const int MaxChunkSlots = 65536;

    /// <summary>The row table entry a chunk spends per slot, a varint id, a flags byte and a varint length.</summary>
    const int RowTableEntryBytes = 8;

    readonly SortedDictionary<ushort, ContentTypeRegistration> _byId = new();
    readonly Dictionary<string, ContentTypeRegistration> _byKey = new(StringComparer.Ordinal);
    ContentTypeRegistration[]? _sorted;

    /// <summary>True once the first pack has loaded, after which a registration throws.</summary>
    public bool IsFrozen { get; private set; }

    /// <summary>Every registration, sorted ASCENDING by type id, always.</summary>
    public IReadOnlyList<ContentTypeRegistration> ByTypeId => _sorted ??= _byId.Values.ToArray();

    /// <summary>
    /// Registers one content type. Runs ONCE, at process start, before any pack is loaded.
    /// </summary>
    /// <param name="band">The range the caller is entitled to, checked against <paramref name="typeId"/>.</param>
    /// <param name="typeId">The stable numeric id. <c>0</c> is reserved and never valid.</param>
    /// <param name="typeKey">The stable string key, unique across the whole registry.</param>
    /// <param name="codec">The row codec, whose written fields are checked against the schema.</param>
    /// <param name="validator">The type's own validator, or null.</param>
    /// <param name="schema">The ordered field list.</param>
    /// <param name="defaultVisibility">The visibility a field inherits when it declares none.</param>
    /// <param name="chunkSlots">Id slots per chunk, a power of two between 256 and 65,536.</param>
    /// <param name="maxRowBytes">This type's row cap, at most <see cref="ContentPackFormat.MaxContentRowBytes"/>.</param>
    /// <param name="maxDefinitionId">A per-type id ceiling a format imposes, or null.</param>
    /// <param name="loadIndex">A derived table built at load, or null.</param>
    /// <exception cref="ContentRegistrationException">Any registration rule of contracts 4.2 to 4.5 or 4.7 is broken.</exception>
    public void RegisterContentType(
        ContentRegistrationBand band,
        ushort typeId,
        string typeKey,
        IContentRowCodec codec,
        IContentValidator? validator,
        ContentFieldSchema schema,
        ContentVisibility defaultVisibility,
        int chunkSlots,
        int maxRowBytes = ContentPackFormat.DefaultMaxRowBytes,
        int? maxDefinitionId = null,
        IContentLoadIndex? loadIndex = null)
    {
        ArgumentNullException.ThrowIfNull(typeKey);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(schema);

        if (IsFrozen)
        {
            throw new ContentRegistrationException(FormattableString.Invariant(
                $"The content type registry is frozen, so type {typeId} '{typeKey}' cannot register. Registration runs once at process start, before the first pack loads."));
        }

        if (typeKey.Length == 0)
        {
            throw new ContentRegistrationException(FormattableString.Invariant(
                $"Content type {typeId} declares an empty type key."));
        }

        CheckBand(band, typeId, typeKey);
        CheckNotAlreadyRegistered(typeId, typeKey);
        CheckChunkShape(typeId, typeKey, chunkSlots, maxRowBytes);
        CheckCodecAgainstSchema(typeId, typeKey, codec, schema);

        var registration = new ContentTypeRegistration(
            band,
            new ContentTypeId(typeId),
            typeKey,
            codec,
            validator,
            schema,
            defaultVisibility,
            chunkSlots,
            maxRowBytes,
            maxDefinitionId,
            loadIndex);

        _byId.Add(typeId, registration);
        _byKey.Add(typeKey, registration);
        _sorted = null;
    }

    /// <summary>
    /// Freezes the registry, which the first pack load does. Idempotent, so a second boot path calling it
    /// is not an error and lookup keeps answering afterwards.
    /// </summary>
    public void Freeze() => IsFrozen = true;

    /// <summary>Looks a registration up by type id.</summary>
    public bool TryGet(ContentTypeId type, [MaybeNullWhen(false)] out ContentTypeRegistration registration)
        => _byId.TryGetValue(type.Value, out registration);

    /// <summary>Looks a registration up by type key, ORDINALLY.</summary>
    public bool TryGetByKey(string typeKey, [MaybeNullWhen(false)] out ContentTypeRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(typeKey);
        return _byKey.TryGetValue(typeKey, out registration);
    }

    static void CheckBand(ContentRegistrationBand band, ushort typeId, string typeKey)
    {
        if (typeId == 0)
        {
            throw new ContentRegistrationException(FormattableString.Invariant(
                $"Type id 0 is reserved and is never a valid content type id, so '{typeKey}' cannot take it."));
        }

        var id = new ContentTypeId(typeId);
        bool inBand = band switch
        {
            ContentRegistrationBand.Engine => id.IsEngine,
            ContentRegistrationBand.Instances => id.IsInstances,
            ContentRegistrationBand.Game => id.IsGame,
            _ => false,
        };

        if (inBand)
        {
            return;
        }

        (int low, int high) = band switch
        {
            ContentRegistrationBand.Engine => (1, 255),
            ContentRegistrationBand.Instances => (256, 1023),
            ContentRegistrationBand.Game => (1024, 65535),
            _ => (0, 0),
        };

        throw new ContentRegistrationException(FormattableString.Invariant(
            $"The {band} band covers type ids {low} to {high}, so it cannot register type {typeId} '{typeKey}'. The band says which ids the caller is entitled to and is not a capability."));
    }

    void CheckNotAlreadyRegistered(ushort typeId, string typeKey)
    {
        if (_byId.TryGetValue(typeId, out ContentTypeRegistration? byId))
        {
            throw new ContentRegistrationException(FormattableString.Invariant(
                $"Type id {typeId} is already registered as '{byId.TypeKey}', so '{typeKey}' cannot take it."));
        }

        if (_byKey.TryGetValue(typeKey, out ContentTypeRegistration? byKey))
        {
            throw new ContentRegistrationException(FormattableString.Invariant(
                $"Type key '{typeKey}' is already registered as type id {byKey.Type.Value}, and a type key is unique across the whole registry."));
        }
    }

    static void CheckChunkShape(ushort typeId, string typeKey, int chunkSlots, int maxRowBytes)
    {
        if (chunkSlots < MinChunkSlots || chunkSlots > MaxChunkSlots || (chunkSlots & (chunkSlots - 1)) != 0)
        {
            throw new ContentRegistrationException(FormattableString.Invariant(
                $"Type {typeId} '{typeKey}' declares {chunkSlots} chunk slots, which must be a power of two between {MinChunkSlots} and {MaxChunkSlots}."));
        }

        if (maxRowBytes < 1 || maxRowBytes > ContentPackFormat.MaxContentRowBytes)
        {
            throw new ContentRegistrationException(FormattableString.Invariant(
                $"Type {typeId} '{typeKey}' declares a row cap of {maxRowBytes} bytes, which must be between 1 and the absolute ceiling of {ContentPackFormat.MaxContentRowBytes}."));
        }

        long worstCase = ((long)chunkSlots * (maxRowBytes + RowTableEntryBytes)) + ContentPackFormat.ChunkHeaderBytes;
        if (worstCase > ContentPackFormat.MaxChunkUncompressedBytes)
        {
            throw new ContentRegistrationException(FormattableString.Invariant(
                $"Type {typeId} '{typeKey}' would build a chunk no reader loads: {chunkSlots} slots at a row cap of {maxRowBytes} bytes is {worstCase} uncompressed bytes, over the {ContentPackFormat.MaxChunkUncompressedBytes} byte ceiling. Lower the slot count or the row cap."));
        }
    }

    static void CheckCodecAgainstSchema(ushort typeId, string typeKey, IContentRowCodec codec, ContentFieldSchema schema)
    {
        IReadOnlyList<string> written = codec.WrittenFields
            ?? throw new ContentRegistrationException(FormattableString.Invariant(
                $"Type {typeId} '{typeKey}' has a codec that declares no written field list."));

        var declared = new HashSet<string>(StringComparer.Ordinal);
        var markers = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContentFieldEntry field in schema.Fields)
        {
            _ = field.IsDerivedMarker ? markers.Add(field.Name) : declared.Add(field.Name);
        }

        var writtenSet = new HashSet<string>(written, StringComparer.Ordinal);
        string[] markersWritten = writtenSet.Where(markers.Contains).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (markersWritten.Length > 0)
        {
            throw new ContentRegistrationException(FormattableString.Invariant(
                $"Type {typeId} '{typeKey}' has a codec writing {string.Join(", ", markersWritten)}, which its schema declares as a derived marker carrying no bytes."));
        }

        string[] missing = declared.Except(writtenSet).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        string[] extra = writtenSet.Except(declared).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (missing.Length == 0 && extra.Length == 0)
        {
            return;
        }

        string missingText = missing.Length == 0 ? "nothing" : string.Join(", ", missing);
        string extraText = extra.Length == 0 ? "nothing" : string.Join(", ", extra);
        throw new ContentRegistrationException(FormattableString.Invariant(
            $"Type {typeId} '{typeKey}' has a codec and a schema that disagree. The schema declares and the codec does not write: {missingText}. The codec writes and the schema does not declare: {extraText}."));
    }
}
