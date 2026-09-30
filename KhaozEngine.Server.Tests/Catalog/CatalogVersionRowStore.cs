using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog;

/// <summary>Counts page reads while forwarding the unchanged authoring contract to a real store.</summary>
public class CatalogPagedRowStore : DispatchProxy
{
    /// <summary>The real store whose published rows the actions read.</summary>
    public IContentAuthoringStore Inner { get; set; } = null!;

    /// <summary>The version and offset requested by each page read.</summary>
    public List<(int Version, int Skip)> Pages { get; } = [];

    /// <summary>Builds a wrapper that implements only the existing required authoring contract.</summary>
    internal static IContentAuthoringStore Wrap<T>(IContentAuthoringStore inner, out T counter)
        where T : CatalogPagedRowStore
    {
        IContentAuthoringStore store = Create<IContentAuthoringStore, T>();
        counter = (T)store;
        counter.Inner = inner;
        return store;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == nameof(IContentAuthoringStore.ListRowsAsync))
        {
            Pages.Add(((int)args![1]!, (int)args[4]!));
        }

        try
        {
            return targetMethod.Invoke(Inner, args);
        }
        catch (TargetInvocationException failure) when (failure.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(failure.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>The wrapper used to prove that a published diff avoids every page read.</summary>
public class CatalogBulkRowStore : CatalogPagedRowStore, IContentVersionRowSource
{
    /// <summary>The versions requested through the optional bulk boundary.</summary>
    public List<int> Versions { get; } = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentRowRevision>> ReadVersionRowsAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        Versions.Add(versionNumber);
        return ((IContentVersionRowSource)Inner).ReadVersionRowsAsync(versionNumber, cancellationToken);
    }
}
