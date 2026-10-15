using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Logging;

[Collection("AllocSensitive")]
public sealed class AmbientFacadeAllocationTests(ITestOutputHelper output)
{
    private sealed class FreshFacades : AssemblyLoadContext
    {
        private readonly string directory = Path.GetDirectoryName(typeof(AmbientFacadeAllocationTests).Assembly.Location)!;

        internal FreshFacades() : base(isCollectible: true) { }

        protected override Assembly? Load(AssemblyName name)
        {
            // Keep the tested facade statics fresh regardless of xUnit order. Share runtime and xUnit only.
            if (name.Name is "KhaozEngine.App" or "KhaozEngine.Diagnostics" or "KhaozEngine.Foundation.Tests")
                return LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll"));
            return null;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualFacadeReadsHaveNoPerCallAllocations(bool includeScopes)
    {
        var context = new FreshFacades();
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(AmbientFacadeAllocationTests).Assembly.Location);
            var probe = assembly.GetType("KhaozEngine.Tests.Logging.AmbientFacadeAllocationProbe", throwOnError: true)!;
            var bytes = (long[])probe.GetMethod("Run")!.Invoke(null, new object[] { includeScopes })!;
            output.WriteLine("1024 calls per window, localization then cached logging. " +
                "Before first Use, scoped, restored bytes = " + string.Join(", ", bytes));
            Assert.All(bytes, value => Assert.Equal(0, value));
        }
        finally { context.Unload(); }
    }
}
