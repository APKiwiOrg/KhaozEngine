using System;

namespace KhaozEngine.MapDoc.Identity;

/// <summary>The common build-identity options contract for both authored identity schemes.</summary>
internal static class MapAuthoredIdentityOptions
{
    internal static void Require(MapResolveOptions options, int resolverVersion, string message)
    {
        if (options.ResolverVersion != resolverVersion || string.IsNullOrWhiteSpace(options.BuilderId) ||
            options.BuilderVersion <= 0 || string.IsNullOrWhiteSpace(options.OptionsHash))
        {
            string field = options.ResolverVersion != resolverVersion ? nameof(options.ResolverVersion)
                : string.IsNullOrWhiteSpace(options.BuilderId) ? nameof(options.BuilderId)
                : options.BuilderVersion <= 0 ? nameof(options.BuilderVersion) : nameof(options.OptionsHash);
            throw new MapDocumentException(field == nameof(options.ResolverVersion) ? MapDocumentRefusal.ResolverContractMismatch
                : field == nameof(options.BuilderVersion) ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.SchemaValueRequired, message)
            {
                FieldPath = field,
                OptionsResolverVersion = options.ResolverVersion,
                ExpectedResolverVersion = resolverVersion,
                ActualText = field == nameof(options.BuilderId) ? options.BuilderId : field == nameof(options.OptionsHash) ? options.OptionsHash : null,
                ActualInteger = field == nameof(options.BuilderVersion) ? options.BuilderVersion : null,
                MinimumInteger = field == nameof(options.BuilderVersion) ? 1 : null,
            };
        }
    }
}
