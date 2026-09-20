using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Serializes current-thread allocation measurements in this test assembly.</summary>
[CollectionDefinition("AllocSensitive", DisableParallelization = true)]
public sealed class AllocSensitiveCollection { }
