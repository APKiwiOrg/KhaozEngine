using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests.App;

[Collection("AmbientLocalization")]
public sealed class LocalizationScopeTests
{
    private static readonly LocalizedText Text = new StringId("scope.key");
    private static DictionaryCatalog Catalog(string value) => new DictionaryCatalog().Add("scope.key", value);

    [Fact]
    public void NestedScopesRestoreAfterExceptionAndRepeatDisposeIsHarmless()
    {
        var previous = LocalizationContext.Catalog;
        try
        {
            LocalizationContext.Catalog = Catalog("process");
            var outer = LocalizationContext.Use(Catalog("outer"));
            try
            {
                Assert.Equal("outer", Text.Resolve());
                Assert.Throws<InvalidOperationException>((Action)(() =>
                {
                    using var inner = LocalizationContext.Use(Catalog("inner"));
                    Assert.Equal("inner", Text.Resolve());
                    throw new InvalidOperationException();
                }));
                Assert.Equal("outer", Text.Resolve());
            }
            finally { outer.Dispose(); }
            outer.Dispose();
            Assert.Equal("process", Text.Resolve());
        }
        finally { LocalizationContext.Catalog = previous; }
    }

    [Fact]
    public void NullScopeOverridesProcessCatalogAndSettersStillUpdateProcessFallback()
    {
        var previous = LocalizationContext.Catalog;
        try
        {
            LocalizationContext.Catalog = Catalog("process");
            using (LocalizationContext.Use(null))
            {
                LocalizationContext.Catalog = Catalog("updated");
                Assert.Null(LocalizationContext.Catalog);
                Assert.Equal("scope.key", Text.Resolve());
                Assert.Equal("explicit", Text.Resolve(Catalog("explicit")));
                using (LocalizationContext.Use(Catalog("inner"))) Assert.Equal("inner", Text.Resolve());
                Assert.Equal("scope.key", Text.Resolve());
            }
            Assert.Equal("updated", Text.Resolve());
        }
        finally { LocalizationContext.Catalog = previous; }
    }

    [Fact]
    public void OutOfOrderDisposalThrowsWithoutChangingTheCurrentScope()
    {
        using var outer = LocalizationContext.Use(Catalog("outer"));
        var inner = LocalizationContext.Use(Catalog("inner"));
        try
        {
            Assert.Throws<InvalidOperationException>(outer.Dispose);
            Assert.Equal("inner", Text.Resolve());
        }
        finally { inner.Dispose(); }
        inner.Dispose();
        Assert.Equal("outer", Text.Resolve());
    }

    [Fact]
    public async Task ConcurrentChildrenInheritButCannotMutateTheirParentOrSibling()
    {
        var previous = LocalizationContext.Catalog;
        try
        {
            LocalizationContext.Catalog = Catalog("process");
            using var parent = LocalizationContext.Use(Catalog("parent"));
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task first = Task.Run(async () =>
            {
                Assert.Equal("parent", Text.Resolve());
                parent.Dispose();
                parent.Dispose();
                Assert.Equal("process", Text.Resolve());
                using var child = LocalizationContext.Use(Catalog("first"));
                changed.SetResult();
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("first", Text.Resolve());
            });
            Task second = Task.Run(async () =>
            {
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("parent", Text.Resolve());
                using (LocalizationContext.Use(Catalog("second")))
                {
                    await Task.Yield();
                    Assert.Equal("second", Text.Resolve());
                }
                Assert.Equal("parent", Text.Resolve());
                observed.SetResult();
            });
            await Task.WhenAll(first, second);
            Assert.Equal("parent", Text.Resolve());
            parent.Dispose();
            Assert.Equal("process", Text.Resolve());
        }
        finally { LocalizationContext.Catalog = previous; }
    }

    [Fact]
    public async Task ParentDisposeDoesNotEraseAnAlreadyInheritedChildScope()
    {
        var previous = LocalizationContext.Catalog;
        try
        {
            LocalizationContext.Catalog = Catalog("process");
            using var parent = LocalizationContext.Use(Catalog("parent"));
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task child = Task.Run(async () =>
            {
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("parent", Text.Resolve());
                parent.Dispose();
                Assert.Equal("process", Text.Resolve());
            });
            parent.Dispose();
            release.SetResult();
            await child;
            Assert.Equal("process", Text.Resolve());
        }
        finally { LocalizationContext.Catalog = previous; }
    }

    [Fact]
    public void WireResxInsideScopeUpdatesOnlyTheProcessCatalog()
    {
        var previous = LocalizationContext.Catalog;
        try
        {
            ResourceStringCatalog installed;
            using (LocalizationContext.Use(Catalog("scope")))
            {
                installed = LocalizationContext.WireResx("Unused.Resources", typeof(LocalizationScopeTests).Assembly);
                Assert.Equal("scope", Text.Resolve());
            }
            Assert.Same(installed, LocalizationContext.Catalog);
        }
        finally { LocalizationContext.Catalog = previous; }
    }

    [Fact]
    public async Task SuppressedFlowSeesOnlyTheProcessDefaultAndCannotRemoveTheParentScope()
    {
        var previous = LocalizationContext.Catalog;
        try
        {
            LocalizationContext.Catalog = Catalog("process");
            using var parent = LocalizationContext.Use(Catalog("parent"));
            Task child;
            using (ExecutionContext.SuppressFlow())
                child = Task.Run(() =>
                {
                    parent.Dispose();
                    Assert.Equal("process", Text.Resolve());
                    using (LocalizationContext.Use(Catalog("child"))) Assert.Equal("child", Text.Resolve());
                    Assert.Equal("process", Text.Resolve());
                });
            await child;
            Assert.Equal("parent", Text.Resolve());
        }
        finally { LocalizationContext.Catalog = previous; }
    }
}
