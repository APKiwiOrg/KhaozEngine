using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Serializes current-thread allocation and process-wide heap measurements in this test assembly.</summary>
[CollectionDefinition("AllocSensitive", DisableParallelization = true)]
public sealed class AllocSensitiveCollection { }
