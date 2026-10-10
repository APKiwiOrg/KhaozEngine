using System;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;

namespace KhaozEngine.MapDoc;

/// <summary>Routes a native document to its resolver by resolver identity. Identity (1, 1) resolves with
/// <see cref="MapResolver"/> over a caller-supplied legacy support height. Identity (1, 2) captures the document's own
/// surfaces and resolves with <see cref="MapResolverV2"/>. The caller loads and verifies the closure first, so local
/// checks that must precede resource reads stay with the caller.</summary>
public static class MapNativeResolution
{
    /// <summary>Resolves <paramref name="document"/> against <paramref name="assets"/>. Identity (1, 1) requires
    /// resolver-1 <paramref name="options"/> and <paramref name="legacySupportHeight"/>. Identity (1, 2) requires
    /// resolver-2 options and ignores <paramref name="legacySupportHeight"/>. Throws
    /// <see cref="MapDocumentException"/> for any other identity, a resolver version that does not match the
    /// identity, a missing legacy support height and every refusal of the chosen resolver.</summary>
    public static MapResolvedDocument Resolve(MapDocument document, MapAssetClosure assets, MapResolveOptions options,
        Func<float, float, float>? legacySupportHeight = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(options);
        int resolver = RequireMatchingOptions(document.ResolverIdentity, options);
        if (resolver == 2)
            return MapResolverV2.Resolve(document, assets, MapDocumentSurfaceSource.Capture(document), options).Document;
        if (legacySupportHeight is null)
            throw new MapDocumentException(
                "Native resolver identity (1, 1) places unbound placements on the analytic terrain and requires a legacy support height.");
        return MapResolver.Resolve(document, assets, legacySupportHeight, options);
    }

    /// <summary>The resolver version a supported native identity selects. Throws <see cref="MapDocumentException"/>
    /// naming any other identity.</summary>
    internal static int ResolverVersionOf(MapResolverIdentityDoc? identity) => identity switch
    {
        { PayloadVersion: 1, ResolverVersion: 1 } => 1,
        { PayloadVersion: 1, ResolverVersion: 2 } => 2,
        _ => throw new MapDocumentException($"Unsupported native resolver identity {Describe(identity)}."),
    };

    /// <summary>The resolver version <paramref name="identity"/> selects, after refusing an unsupported identity and
    /// then options whose resolver version differs.</summary>
    internal static int RequireMatchingOptions(MapResolverIdentityDoc? identity, MapResolveOptions options)
    {
        int resolver = ResolverVersionOf(identity);
        if (options.ResolverVersion != resolver)
            throw new MapDocumentException($"Native resolver identity {Describe(identity)} " +
                $"does not match options resolver version {options.ResolverVersion}.");
        return resolver;
    }

    /// <summary>The identity as "(payload, resolver)", or "(missing)".</summary>
    internal static string Describe(MapResolverIdentityDoc? identity) => identity is null
        ? "(missing)" : $"({identity.PayloadVersion}, {identity.ResolverVersion})";
}
