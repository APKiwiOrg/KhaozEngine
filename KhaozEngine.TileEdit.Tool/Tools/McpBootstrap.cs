using Microsoft.Extensions.DependencyInjection;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>The single composition path shared by the real stdio host and the in-process integration test, so
/// both wire up the same services and the same tool set. <see cref="AddTileEditServices"/> registers the stateful
/// singletons the tool classes are constructed against, and <see cref="WithTileEditTools"/> registers the tool
/// classes themselves. Keeping the tool list in one place means the host and the tests can never drift apart on
/// which verbs the server exposes. Both are empty scaffolds until the session, the services and the verb classes
/// land, and every later addition goes here rather than into Program.cs.</summary>
public static class McpBootstrap
{
    /// <summary>Registers the session and the query, mutation, and render services as singletons, so the MCP
    /// server resolves one shared session for the whole process. Registers nothing yet.</summary>
    public static IServiceCollection AddTileEditServices(this IServiceCollection services) => services;

    /// <summary>Registers the verb classes on the MCP server builder, so both the host and the tests pick up the
    /// same verb set from this one call. Registers nothing yet.</summary>
    public static IMcpServerBuilder WithTileEditTools(this IMcpServerBuilder builder) => builder;
}
