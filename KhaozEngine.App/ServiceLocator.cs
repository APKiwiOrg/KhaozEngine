using System;
using System.Collections.Concurrent;

namespace KhaozEngine.App;

/// <summary>
/// Lightweight service locator for registering and resolving game systems by interface type.
/// Prefer this over tight coupling between systems. Implements <see cref="IServiceProvider"/> so it
/// can be stashed in the KhaozEngine ScreenManager's <c>Services</c> slot and cast back by screens.
/// Backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>, so register / replace / resolve are
/// safe under concurrent access.
/// </summary>
public sealed class ServiceLocator : IServiceProvider
{
    private readonly ConcurrentDictionary<Type, object> services = new();

    /// <summary>
    /// Registers a service instance under the given interface type.
    /// </summary>
    /// <typeparam name="T">The interface type to register under.</typeparam>
    /// <param name="service">The service instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A service of type <typeparamref name="T"/> is already registered.</exception>
    public void Register<T>(T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        Type type = typeof(T);

        if (!services.TryAdd(type, service))
        {
            throw new InvalidOperationException($"Service of type {type.Name} is already registered.");
        }
    }

    /// <summary>
    /// Replaces an existing service registration or adds a new one.
    /// </summary>
    /// <typeparam name="T">The interface type to register under.</typeparam>
    /// <param name="service">The service instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is null.</exception>
    public void Replace<T>(T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        services[typeof(T)] = service;
    }

    /// <summary>
    /// Resolves a registered service by interface type.
    /// </summary>
    /// <typeparam name="T">The interface type to resolve.</typeparam>
    /// <returns>The registered service instance.</returns>
    /// <exception cref="InvalidOperationException">No service of type <typeparamref name="T"/> is registered.</exception>
    public T Get<T>() where T : class
    {
        Type type = typeof(T);

        if (services.TryGetValue(type, out object? service))
        {
            return (T)service;
        }

        throw new InvalidOperationException($"Service of type {type.Name} is not registered.");
    }

    /// <summary>
    /// Attempts to resolve a registered service. Returns null if not found.
    /// </summary>
    /// <typeparam name="T">The interface type to resolve.</typeparam>
    /// <returns>The service instance, or null if not registered.</returns>
    public T? TryGet<T>() where T : class
    {
        return services.TryGetValue(typeof(T), out object? service) ? (T)service : null;
    }

    /// <summary>
    /// Returns true if a service of the given type is registered.
    /// </summary>
    /// <typeparam name="T">The interface type to check.</typeparam>
    public bool Has<T>() where T : class
    {
        return services.ContainsKey(typeof(T));
    }

    /// <summary>
    /// <see cref="IServiceProvider"/> implementation: resolves a registered service by runtime
    /// type, or null if not registered. Never throws.
    /// </summary>
    /// <param name="serviceType">The service type to resolve.</param>
    /// <returns>The registered service instance, or null.</returns>
    public object? GetService(Type serviceType)
    {
        return services.TryGetValue(serviceType, out object? service) ? service : null;
    }
}
