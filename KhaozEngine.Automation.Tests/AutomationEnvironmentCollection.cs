using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Serial collection for test classes that write the process-global <c>KE_AUTOMATION</c> environment variable
/// (the host's gate 2). Environment variables are per-process, so a class that sets one and restores it in a
/// <c>finally</c> leaves a window in which every other class in the assembly reads the other value. The
/// <c>DisableParallelization</c> on the definition is what does the work.
/// </summary>
[CollectionDefinition("AutomationEnvironment", DisableParallelization = true)]
public sealed class AutomationEnvironmentCollection { }
