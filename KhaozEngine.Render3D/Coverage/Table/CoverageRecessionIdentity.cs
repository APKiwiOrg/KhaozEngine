namespace KhaozEngine.Render3D;

/// <summary>
/// Six lowercase SHA-256 digests binding the exact host, semantic host map, complete authored equipment
/// configuration, fixed refinement recipes, carry arithmetic and consumer-owned state schema.
/// </summary>
/// <remarks>
/// Configuration includes sorted part and command IDs, exact attachment and companion identities,
/// command values, static local/socket/body-alignment transforms and host contract bindings.
/// It excludes actor incarnation IDs, frame IDs, current palettes and world/model poses.
/// Refinement includes source ordinals and recipes. Hash numeric inputs explicitly in little-endian order,
/// never as native-layout struct bytes. This type carries digests, it does not compute them.
/// </remarks>
public readonly record struct CoverageRecessionIdentity(
    string HostSha256, string HostMapSha256, string ConfigurationSha256,
    string RefinementSha256, string ArithmeticSha256, string StateSchemaSha256);
