using System;
using Xunit;

namespace KhaozEngine.Tests.Catalog.AzureBlob;

/// <summary>
/// A <see cref="FactAttribute"/> that is SKIPPED unless the environment variable
/// <c>KE_PACKSTORE_BLOB</c> holds the URL of a reachable blob container. CI has no storage account, so this
/// leg runs only locally or against a throwaway container on demand. Mirrors
/// <c>KhaozEngine.Tests.Catalog.CatalogSqlServerFactAttribute</c>.
/// <para>
/// It also insists the container NAME carry <see cref="ContainerMarker"/>, which the SQL Server legs do with
/// their database names for the same reason: the leg deletes what it writes, so the one thing that must be
/// impossible is pointing it at a live content origin and having it prune a published chunk.
/// </para>
/// </summary>
public sealed class AzureBlobPackStoreFactAttribute : FactAttribute
{
    /// <summary>The variable naming the container, read once per fact at construction.</summary>
    public const string EnvironmentVariable = "KE_PACKSTORE_BLOB";

    /// <summary>What the container's name must contain before the leg writes a byte into it.</summary>
    public const string ContainerMarker = "-packstore-test-";

    /// <summary>Skips unless <see cref="EnvironmentVariable"/> names a marked container.</summary>
    public AzureBlobPackStoreFactAttribute()
    {
        string? value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrEmpty(value))
        {
            Skip = "set " + EnvironmentVariable + " to a container URL to run";
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !ContainerName(uri).Contains(ContainerMarker, StringComparison.Ordinal))
        {
            Skip = EnvironmentVariable + " must name a container whose name carries '" + ContainerMarker
                + "', because this leg deletes what it writes";
        }
    }

    /// <summary>The container's own name, which is the last path segment of its URL.</summary>
    /// <param name="containerUri">The container URL, with or without a shared-access signature.</param>
    /// <exception cref="ArgumentNullException"><paramref name="containerUri"/> is null.</exception>
    public static string ContainerName(Uri containerUri)
    {
        ArgumentNullException.ThrowIfNull(containerUri);
        string[] segments = containerUri.Segments;
        return segments.Length == 0 ? string.Empty : segments[^1].Trim('/');
    }
}
