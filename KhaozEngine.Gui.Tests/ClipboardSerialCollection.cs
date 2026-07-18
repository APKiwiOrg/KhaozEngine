using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Serial collection for test classes that mutate the process-global <c>Clipboard</c> text provider
/// (<c>RegisterTextProvider</c>/<c>ClearTextProvider</c>). xUnit parallelizes across classes by default and the
/// provider is a static seam, so classes touching it (ClipboardTests, TextEntryTests' paste path) opt into this
/// non-parallel collection to keep the shared state deterministic.
/// </summary>
[CollectionDefinition("ClipboardSerial", DisableParallelization = true)]
public sealed class ClipboardSerialCollection { }
