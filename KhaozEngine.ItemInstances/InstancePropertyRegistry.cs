using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// One registered property kind: everything <see cref="InstancePropertyRegistry.Register"/> was handed,
/// held immutably so the payload codec, the remap pass and the validator all read the same declaration.
/// </summary>
public sealed class InstancePropertyRegistration
{
    internal InstancePropertyRegistration(
        InstanceKindBand band,
        ushort kind,
        IInstancePropertyCodec codec,
        PropertyVisibility visibility,
        int identificationMaskBit,
        InstanceFieldShape shape,
        ReadOnlyMemory<InstanceReferenceTarget> references)
    {
        Band = band;
        Kind = kind;
        Codec = codec;
        Visibility = visibility;
        IdentificationMaskBit = identificationMaskBit;
        Shape = shape;
        References = references;
    }

    /// <summary>The band the registering caller claimed, and the range its kind was checked against.</summary>
    public InstanceKindBand Band { get; }

    /// <summary>The property kind id, which is the tag written into every payload carrying this field.</summary>
    public ushort Kind { get; }

    /// <summary>The kind's own rules over its field body, beyond what <see cref="Shape"/> already says.</summary>
    public IInstancePropertyCodec Codec { get; }

    /// <summary>How far this field travels, compared with a <c>&lt;=</c> against a viewer's clearance.</summary>
    public PropertyVisibility Visibility { get; }

    /// <summary>
    /// The FIXED bit of kind 128's revealed mask this field is gated behind, or <c>-1</c> when it is not
    /// identification gated. Never the kind's position in the ascending list of gated kinds.
    /// </summary>
    public int IdentificationMaskBit { get; }

    /// <summary>Whether this field is hidden until its <see cref="IdentificationMaskBit"/> is revealed.</summary>
    public bool IsIdentificationGated => IdentificationMaskBit >= 0;

    /// <summary>Where this kind's values sit in its field bytes.</summary>
    public InstanceFieldShape Shape { get; }

    /// <summary>Which of those slots hold content ids, and which type each id belongs to.</summary>
    public ReadOnlyMemory<InstanceReferenceTarget> References { get; }
}

/// <summary>
/// The property kind registry of spec 3.3: registration once at process start, the freeze at the first pack
/// load, and lookup by kind.
/// <para>
/// It is a map keyed by KIND and it is NOT a static, following <c>ContentTypeRegistry</c> one level up.
/// Per instance, deliberately, which is what lets a decoder be handed a registry that deliberately omits a
/// kind, keeps every test free of process-global state, and means nothing here needs a
/// <c>DisableParallelization</c> collection.
/// </para>
/// <para>
/// Every refusal is a THROW at startup rather than a silent acceptance, because each one is a programming
/// error in a registration and none of them is data. Nothing on this path throws for a byte.
/// </para>
/// </summary>
public sealed class InstancePropertyRegistry
{
    /// <summary>The highest bit of kind 128's revealed mask, which is a <c>uint32</c>.</summary>
    public const int MaxIdentificationMaskBit = 31;

    readonly SortedDictionary<ushort, InstancePropertyRegistration> _byKind = new();
    readonly Dictionary<int, ushort> _byMaskBit = new();
    InstancePropertyRegistration[]? _sorted;

    /// <summary>True once the first pack has loaded, after which a registration throws.</summary>
    public bool IsFrozen { get; private set; }

    /// <summary>Every registration, sorted ASCENDING by kind, always.</summary>
    public IReadOnlyList<InstancePropertyRegistration> ByKind => _sorted ??= _byKind.Values.ToArray();

    /// <summary>
    /// Registers one property kind. Runs ONCE, at process start, before any pack is loaded.
    /// </summary>
    /// <param name="band">The range the caller is entitled to, checked against <paramref name="kind"/>.</param>
    /// <param name="kind">The kind id. <c>0</c> is reserved and never valid.</param>
    /// <param name="codec">The kind's own rules over its field body.</param>
    /// <param name="visibility">How far the field travels.</param>
    /// <param name="identificationMaskBit">The fixed revealed-mask bit, or <c>-1</c> when not gated.</param>
    /// <param name="shape">Where the kind's values sit in its field bytes.</param>
    /// <param name="references">Which slots hold content ids, and the type each belongs to.</param>
    /// <exception cref="InvalidOperationException">The registry is already frozen.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The kind is 0, outside the claimed band, or the mask
    /// bit is outside <c>-1</c> to <see cref="MaxIdentificationMaskBit"/>.</exception>
    /// <exception cref="ArgumentException">The kind or the mask bit is already taken, or the shape and its
    /// references disagree.</exception>
    public void Register(
        InstanceKindBand band,
        ushort kind,
        IInstancePropertyCodec codec,
        PropertyVisibility visibility,
        int identificationMaskBit,
        in InstanceFieldShape shape,
        ReadOnlySpan<InstanceReferenceTarget> references)
    {
        ArgumentNullException.ThrowIfNull(codec);

        if (IsFrozen)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"The instance property registry is frozen, so kind {kind} cannot register. Registration runs once at process start, before the first pack loads."));
        }

        CheckBand(band, kind);
        CheckMaskBit(kind, identificationMaskBit);
        CheckShape(kind, shape, references);

        if (_byKind.TryGetValue(kind, out InstancePropertyRegistration? held))
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Kind {kind} is already registered in the {held.Band} band. A kind is never unregistered and a codec is never replaced: either would make two previously distinct items stack and destroy one identity."),
                nameof(kind));
        }

        if (identificationMaskBit >= 0 && _byMaskBit.TryGetValue(identificationMaskBit, out ushort owner))
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Identification mask bit {identificationMaskBit} already belongs to kind {owner}, so kind {kind} cannot take it. A new gated kind takes the NEXT FREE bit, never one a released engine has used, because the mask is stored in every partially identified item."),
                nameof(identificationMaskBit));
        }

        var registration = new InstancePropertyRegistration(
            band,
            kind,
            codec,
            visibility,
            identificationMaskBit,
            shape,
            references.ToArray());

        _byKind.Add(kind, registration);
        if (identificationMaskBit >= 0)
        {
            _byMaskBit.Add(identificationMaskBit, kind);
        }

        _sorted = null;
    }

    /// <summary>
    /// Freezes the registry, which the pack load path does. Idempotent, so a second boot path calling it is
    /// not an error, and lookup keeps answering afterwards.
    /// </summary>
    public void Freeze() => IsFrozen = true;

    /// <summary>Looks a registration up by kind.</summary>
    /// <param name="kind">The kind id.</param>
    /// <param name="registration">The registration, when the kind is registered.</param>
    public bool TryGet(ushort kind, [MaybeNullWhen(false)] out InstancePropertyRegistration registration)
        => _byKind.TryGetValue(kind, out registration);

    /// <summary>Looks the kind gated behind one revealed-mask bit up.</summary>
    /// <param name="maskBit">The bit of kind 128's revealed mask.</param>
    /// <param name="registration">The registration, when a kind claims that bit.</param>
    public bool TryGetByIdentificationMaskBit(
        int maskBit,
        [MaybeNullWhen(false)] out InstancePropertyRegistration registration)
    {
        registration = null;
        return maskBit >= 0
            && _byMaskBit.TryGetValue(maskBit, out ushort kind)
            && _byKind.TryGetValue(kind, out registration);
    }

    static void CheckBand(InstanceKindBand band, ushort kind)
    {
        if (kind == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                "Kind 0 is reserved and is never a valid property kind.");
        }

        bool inBand = band switch
        {
            InstanceKindBand.Engine => kind < InstancePropertyKind.FirstScopeBKind,
            InstanceKindBand.ScopeB => kind >= InstancePropertyKind.FirstScopeBKind
                && kind < InstancePropertyKind.FirstGameKind,
            InstanceKindBand.Game => kind >= InstancePropertyKind.FirstGameKind,
            _ => false,
        };

        if (inBand)
        {
            return;
        }

        (int low, int high) = band switch
        {
            InstanceKindBand.Engine => (1, InstancePropertyKind.FirstScopeBKind - 1),
            InstanceKindBand.ScopeB => (InstancePropertyKind.FirstScopeBKind, InstancePropertyKind.FirstGameKind - 1),
            InstanceKindBand.Game => (InstancePropertyKind.FirstGameKind, ushort.MaxValue),
            _ => (0, 0),
        };

        throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            FormattableString.Invariant(
                $"The {band} band covers kinds {low} to {high}, so it cannot register kind {kind}. The band says which kinds the caller is entitled to and is not a capability."));
    }

    static void CheckMaskBit(ushort kind, int identificationMaskBit)
    {
        if (identificationMaskBit >= -1 && identificationMaskBit <= MaxIdentificationMaskBit)
        {
            return;
        }

        throw new ArgumentOutOfRangeException(
            nameof(identificationMaskBit),
            identificationMaskBit,
            FormattableString.Invariant(
                $"Kind {kind} declares mask bit {identificationMaskBit}, which must be -1 for an ungated kind or 0 to {MaxIdentificationMaskBit} for a gated one, because the revealed mask is a uint32."));
    }

    static void CheckShape(
        ushort kind,
        in InstanceFieldShape shape,
        ReadOnlySpan<InstanceReferenceTarget> references)
    {
        bool repeats = shape.Count != InstanceCountWidth.None;
        if (repeats != !shape.Entry.IsEmpty)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Kind {kind} declares a count width of {shape.Count} and {shape.Entry.Length} entry slots. A field that repeats has entry slots and one that does not has none."),
                nameof(shape));
        }

        foreach (InstanceReferenceTarget target in references)
        {
            if (string.IsNullOrEmpty(target.ContentTypeKey))
            {
                throw new ArgumentException(
                    FormattableString.Invariant($"Kind {kind} declares a reference target with no content type key."),
                    nameof(references));
            }

            int slots = target.Site == InstanceReferenceSite.Header ? shape.Header.Length : shape.Entry.Length;
            if (target.SlotIndex < 0 || target.SlotIndex >= slots)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Kind {kind} points a '{target.ContentTypeKey}' reference at {target.Site} slot {target.SlotIndex}, and that shape has {slots} slots. A target that names a slot the shape does not have is a content id the remap pass would rewrite in the wrong place."),
                    nameof(references));
            }
        }
    }

    /// <summary>
    /// A registry holding the v1 kinds of spec 3.3, the eight engine kinds and the seven Scope B ones, with
    /// the shapes and reference targets that table gives. UNFROZEN, so a game adds its own kinds at or above
    /// 1024 before the first pack loads.
    /// </summary>
    /// <remarks>
    /// These rows are the whole of what the remap pass and the validator walk, so a typo here is a data bug
    /// rather than a test failure. Two of them were defects in an earlier draft of the spec and are covered
    /// by construction now: kind 7's material ids are <c>item</c> references, and a socket's contained
    /// definition id is an <c>item</c> reference at every depth.
    /// <para>
    /// Kinds 128, 131, 132 and 133 register a REAL codec here rather than
    /// <see cref="InstancePropertyCodec.ShapeOnly"/>, and it has to happen at registration: a registered
    /// codec is never replaced (that would make two previously distinct items stack and destroy one
    /// identity), so there is no later moment at which one could be supplied. Every other v1 kind's shape
    /// is its whole contract.
    /// </para>
    /// </remarks>
    public static InstancePropertyRegistry CreateV1()
    {
        var registry = new InstancePropertyRegistry();

        // The engine band, kinds 1 to 127: generic per-instance facts any game might want. Kinds 4, 5 and 6
        // are OwnerOnly while 7 and 8 are Everyone, so visibility is deliberately NOT monotonic in the kind.
        Scalars(registry, InstanceKindBand.Engine, InstancePropertyKind.Flags, 1, PropertyVisibility.Everyone);
        Scalars(registry, InstanceKindBand.Engine, InstancePropertyKind.ItemLevel, 1, PropertyVisibility.Everyone);
        Scalars(registry, InstanceKindBand.Engine, InstancePropertyKind.Quality, 1, PropertyVisibility.Everyone);
        Scalars(registry, InstanceKindBand.Engine, InstancePropertyKind.Charges, 2, PropertyVisibility.OwnerOnly);
        Scalars(registry, InstanceKindBand.Engine, InstancePropertyKind.Durability, 2, PropertyVisibility.OwnerOnly);
        Scalars(registry, InstanceKindBand.Engine, InstancePropertyKind.BoundTo, 1, PropertyVisibility.OwnerOnly);

        // Materials are input item DEFINITIONS, so every material id is an item reference and a retired
        // material is visible to both the rule pass and the validator.
        registry.Register(
            InstanceKindBand.Engine,
            InstancePropertyKind.Materials,
            InstancePropertyCodec.ShapeOnly,
            PropertyVisibility.Everyone,
            NotGated,
            new InstanceFieldShape(default, InstanceCountWidth.Varint, MaterialEntry),
            new[] { new InstanceReferenceTarget(EngineContentTypes.ItemTypeKey, InstanceReferenceSite.Entry, 0) });

        Scalars(registry, InstanceKindBand.Engine, InstancePropertyKind.Tier, 1, PropertyVisibility.Everyone);

        // The Scope B band, kinds 128 to 1023. Kind 128 holds the revealed mask the four gated kinds index
        // into, and it is not itself gated.
        registry.Register(
            InstanceKindBand.ScopeB,
            InstancePropertyKind.Identification,
            InstancePropertyCodec.Identification,
            PropertyVisibility.Everyone,
            NotGated,
            new InstanceFieldShape(ByteThenVarint, InstanceCountWidth.None, default),
            ReadOnlySpan<InstanceReferenceTarget>.Empty);

        registry.Register(
            InstanceKindBand.ScopeB,
            InstancePropertyKind.UniqueTemplate,
            InstancePropertyCodec.ShapeOnly,
            PropertyVisibility.Everyone,
            UniqueTemplateMaskBit,
            new InstanceFieldShape(OneVarint, InstanceCountWidth.None, default),
            new[] { new InstanceReferenceTarget(UniqueTemplateTypeKey, InstanceReferenceSite.Header, 0) });

        // A rarity id is a BYTE rather than a varint, and it is still a content reference: the slot kind and
        // the reference target are independent, which is why the walker reads both.
        registry.Register(
            InstanceKindBand.ScopeB,
            InstancePropertyKind.Rarity,
            InstancePropertyCodec.ShapeOnly,
            PropertyVisibility.Everyone,
            NotGated,
            new InstanceFieldShape(OneByte, InstanceCountWidth.None, default),
            new[] { new InstanceReferenceTarget(RarityRuleTypeKey, InstanceReferenceSite.Header, 0) });

        // Kind 132's count is a VARINT and kind 131's is a BYTE, and the difference is deliberate. Contracts
        // 9.5 writes the socket count as a varint, so narrowing it would be a width change, invisible in the
        // golden file because the worked example writes 01 and that is both. The affix count is Scope B's own
        // and a byte caps affixes at 255, which costs one byte fewer on every affixed item in the world.
        Entries(registry, InstancePropertyKind.Affixes, AffixesMaskBit);

        registry.Register(
            InstanceKindBand.ScopeB,
            InstancePropertyKind.Sockets,
            InstancePropertyCodec.SocketList,
            PropertyVisibility.Everyone,
            NotGated,
            new InstanceFieldShape(default, InstanceCountWidth.Varint, SocketEntry),
            new[]
            {
                new InstanceReferenceTarget(EngineContentTypes.SocketTypeTypeKey, InstanceReferenceSite.Entry, 0),
                new InstanceReferenceTarget(EngineContentTypes.ItemTypeKey, InstanceReferenceSite.Entry, 1),
            });

        Entries(registry, InstancePropertyKind.Enchantments, EnchantmentsMaskBit);

        // The header slot is a rarity_rule id rather than a unique_template one: it records WHICH rarity
        // rule's display format composed the name, which is why a later rarity change does not strip it.
        registry.Register(
            InstanceKindBand.ScopeB,
            InstancePropertyKind.RareName,
            InstancePropertyCodec.ShapeOnly,
            PropertyVisibility.Everyone,
            RareNameMaskBit,
            new InstanceFieldShape(OneVarint, InstanceCountWidth.Byte, OneVarint),
            new[]
            {
                new InstanceReferenceTarget(RarityRuleTypeKey, InstanceReferenceSite.Header, 0),
                new InstanceReferenceTarget(RareNameWordTypeKey, InstanceReferenceSite.Entry, 0),
            });

        return registry;
    }

    /// <summary>The mask bit an ungated kind declares.</summary>
    const int NotGated = -1;

    // The four v1 gated bits, FIXED at registration and pinned by spec 21. A derived index would re-point
    // every partially identified item in the world the moment an engine release added a gated kind below
    // 129, silently, with no byte changing.
    const int UniqueTemplateMaskBit = 0;
    const int AffixesMaskBit = 1;
    const int EnchantmentsMaskBit = 2;
    const int RareNameMaskBit = 3;

    // Scope B's own content type keys, which its phase 4 registers into the catalog. The two engine keys
    // come from EngineContentTypes, so a rename there is a compile error here rather than a silent drift.
    const string UniqueTemplateTypeKey = "unique_template";
    const string RarityRuleTypeKey = "rarity_rule";
    const string ModTypeKey = "mod";
    const string RareNameWordTypeKey = "rare_name_word";

    static readonly InstanceSlotKind[] OneVarint = { InstanceSlotKind.Varint };
    static readonly InstanceSlotKind[] TwoVarints = { InstanceSlotKind.Varint, InstanceSlotKind.Varint };
    static readonly InstanceSlotKind[] OneByte = { InstanceSlotKind.Byte };
    static readonly InstanceSlotKind[] ByteThenVarint = { InstanceSlotKind.Byte, InstanceSlotKind.Varint };
    static readonly InstanceSlotKind[] MaterialEntry = { InstanceSlotKind.Varint, InstanceSlotKind.Varint };

    static readonly InstanceSlotKind[] AffixEntry =
    {
        InstanceSlotKind.Varint, InstanceSlotKind.Byte, InstanceSlotKind.Fixed2, InstanceSlotKind.Varint,
    };

    static readonly InstanceSlotKind[] SocketEntry =
    {
        InstanceSlotKind.Varint, InstanceSlotKind.Varint, InstanceSlotKind.Varint, InstanceSlotKind.NestedPayload,
    };

    static void Scalars(
        InstancePropertyRegistry registry,
        InstanceKindBand band,
        ushort kind,
        int count,
        PropertyVisibility visibility)
        => registry.Register(
            band,
            kind,
            InstancePropertyCodec.ShapeOnly,
            visibility,
            NotGated,
            new InstanceFieldShape(count == 1 ? OneVarint : TwoVarints, InstanceCountWidth.None, default),
            ReadOnlySpan<InstanceReferenceTarget>.Empty);

    /// <summary>Kinds 131 and 133 share one entry layout and differ only in their gated bit.</summary>
    static void Entries(InstancePropertyRegistry registry, ushort kind, int identificationMaskBit)
        => registry.Register(
            InstanceKindBand.ScopeB,
            kind,
            InstancePropertyCodec.AffixList,
            PropertyVisibility.Everyone,
            identificationMaskBit,
            new InstanceFieldShape(default, InstanceCountWidth.Byte, AffixEntry),
            new[] { new InstanceReferenceTarget(ModTypeKey, InstanceReferenceSite.Entry, 0) });
}
