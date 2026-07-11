using Microsoft.Extensions.DependencyInjection;

namespace KhaozEngine.MapEdit.Tools;

/// <summary>The single composition path shared by the real stdio host and the in-process integration test, so
/// both wire up the same services and the same tool set. <see cref="AddMapEditServices"/> registers the stateful
/// singletons the tool classes are constructed against, and <see cref="WithMapEditTools"/> registers the tool
/// classes themselves. Keeping the tool list in one place means the host and the tests can never drift apart on
/// which verbs the server exposes.</summary>
public static class McpBootstrap
{
    /// <summary>Registers the session and the query, mutation, and render services as singletons. The tool classes
    /// take these through their constructors, so the MCP server resolves one shared session for the whole
    /// process.</summary>
    public static IServiceCollection AddMapEditServices(this IServiceCollection services)
    {
        services.AddSingleton<MapEditSession>();
        services.AddSingleton<QueryService>();
        services.AddSingleton<MutationService>();
        services.AddSingleton<RenderService>();
        return services;
    }

    /// <summary>Registers the document, query, mutation, and render tool classes on the MCP server builder, so both
    /// the host and the tests pick up the same verb set from this one call.</summary>
    public static IMcpServerBuilder WithMapEditTools(this IMcpServerBuilder builder)
    {
        builder.WithTools<DocumentTools>();
        builder.WithTools<QueryTools>();
        builder.WithTools<MutationTools>();
        builder.WithTools<RenderTools>();
        return builder;
    }
}
