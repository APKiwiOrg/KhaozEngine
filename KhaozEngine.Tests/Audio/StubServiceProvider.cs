using System;

namespace KhaozEngine.Tests;

/// <summary>A no-service <see cref="IServiceProvider"/> so a headless ContentManager can be constructed.</summary>
internal sealed class StubServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
