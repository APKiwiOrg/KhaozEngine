using Xunit;

namespace KeDungeon.Tests;

/// <summary>Verb tests temporarily replace the process-wide Console.Out and Console.Error writers.</summary>
[CollectionDefinition("DungeonConsole", DisableParallelization = true)]
public sealed class ConsoleCollection;
