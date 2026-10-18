using Xunit;

namespace KhaozEngine.Tests;

/// <summary>The serial collection for this assembly's allocation assertions, which measure in an
/// <see cref="AllocationRegion"/> and so must not run beside another test. Reference it with
/// <c>[Collection("AllocSensitive")]</c>. xUnit collection definitions do not cross assemblies, so each test
/// assembly that measures allocation carries its own copy (see the one in Foundation.Tests).</summary>
[CollectionDefinition("AllocSensitive", DisableParallelization = true)]
public sealed class AllocSensitiveCollection { }
