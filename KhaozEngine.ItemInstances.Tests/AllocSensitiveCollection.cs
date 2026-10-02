using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Runs allocation measurements separately from other collections in this assembly.
/// Collection definitions are per assembly, so the sibling test projects' definitions do not apply here.</summary>
[CollectionDefinition("AllocSensitive", DisableParallelization = true)]
public sealed class AllocSensitiveCollection { }
