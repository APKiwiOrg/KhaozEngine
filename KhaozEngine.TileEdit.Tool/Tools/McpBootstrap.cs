using Microsoft.Extensions.DependencyInjection;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>The single composition path shared by the real stdio host and the in-process integration test, so
/// both wire up the same services and the same tool set. <see cref="AddTileEditServices"/> registers the stateful
/// singletons the tool classes are constructed against, and <see cref="WithTileEditTools"/> registers the tool
/// classes themselves. Keeping the tool list in one place means the host and the tests can never drift apart on
/// which verbs the server exposes, and every later verb class goes here rather than into Program.cs.</summary>
public static class McpBootstrap
{
    /// <summary>Registers the session and the query, mutation, and render services as singletons. The tool classes
    /// take these through their constructors, so the MCP server resolves ONE shared session for the whole
    /// process, which is what makes an open world outlive a single verb call.</summary>
    public static IServiceCollection AddTileEditServices(this IServiceCollection services)
    {
        services.AddSingleton<TileEditSession>();
        services.AddSingleton<QueryService>();
        services.AddSingleton<MutationService>();
        services.AddSingleton<RenderService>();
        return services;
    }

    /// <summary>Registers the world, tile, height, object, marker, prefab, collision, and render verb classes on
    /// the MCP server builder, so both the host and the tests pick up the same verb set from this one call.</summary>
    public static IMcpServerBuilder WithTileEditTools(this IMcpServerBuilder builder)
    {
        builder.WithTools<WorldTools>();
        builder.WithTools<TileTools>();
        builder.WithTools<HeightTools>();
        builder.WithTools<ObjectTools>();
        builder.WithTools<MarkerTools>();
        builder.WithTools<PrefabTools>();
        builder.WithTools<CollisionTools>();
        builder.WithTools<RenderTools>();
        return builder;
    }
}
