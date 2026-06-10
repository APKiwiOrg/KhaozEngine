using System;
using System.Reflection;

namespace KhaozEngine.App;

/// <summary>
/// Reads <see cref="AssemblyMetadataAttribute"/> items (emitted into an assembly by its
/// <c>Directory.Build.props</c>) back at runtime, so a game can surface its own build identity
/// without re-deriving it. The caller supplies the assemblies to probe; this type never calls
/// <see cref="Assembly.GetExecutingAssembly"/> (which would resolve to the engine assembly).
/// </summary>
public static class BuildMetadata
{
    /// <summary>
    /// Probes <paramref name="assemblies"/> in order (skipping null entries) for an
    /// <see cref="AssemblyMetadataAttribute"/> whose <see cref="AssemblyMetadataAttribute.Key"/>
    /// equals <paramref name="key"/> (ordinal) with a non-whitespace value. Returns the first such
    /// value, or <paramref name="fallback"/> if none match.
    /// </summary>
    /// <param name="key">The metadata key to look up.</param>
    /// <param name="fallback">Returned verbatim when no assembly yields a value.</param>
    /// <param name="assemblies">Assemblies to probe, in priority order; null entries are skipped.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    public static string Read(string key, string fallback, params Assembly?[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(key);

        foreach (Assembly? assembly in assemblies)
        {
            if (assembly is null)
            {
                continue;
            }

            if (TryReadFrom(assembly, key, out string value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static bool TryReadFrom(Assembly assembly, string key, out string value)
    {
        object[] attributes = assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false);
        for (int i = 0; i < attributes.Length; i++)
        {
            if (attributes[i] is not AssemblyMetadataAttribute metadata)
            {
                continue;
            }

            if (!string.Equals(metadata.Key, key, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(metadata.Value))
            {
                break;
            }

            value = metadata.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
