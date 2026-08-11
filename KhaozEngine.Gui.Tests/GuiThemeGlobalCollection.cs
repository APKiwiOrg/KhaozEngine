using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Serial collection for test classes that WRITE the process-global <c>GuiTheme.Default</c>
/// (<c>KhaozEngine.Gui/GuiTheme.cs</c>, a mutable static with a public setter that consumers are meant to assign
/// at startup, so it is not going away). xUnit parallelizes across collections, and a class that swaps the
/// ambient theme and restores it in a <c>finally</c> leaves a window in which every other class in the assembly
/// reads the OTHER palette. That is not a theory: issue #349 caught <c>PatchNotesThemeTests</c> asserting
/// <c>Crisp.SurfaceHover</c> and receiving <c>Legacy.SurfaceHover</c> on a CI leg, and later caught
/// <c>RetainedWidgetStyleTests</c> the same way, because <c>GuiStyle.Default</c> recomputes off
/// <c>GuiTheme.Default</c> on every get.
///
/// <para><c>DisableParallelization</c> is what closes it, and it closes it for READERS too. A collection marked
/// this way runs in its own sequential phase with no other collection running (verified on xUnit 2.9.2: the
/// parallel collections all finished before this phase opened), so while the theme is swapped nothing else in
/// the assembly is executing. That matters, because the exposed set is every test that touches a widget default,
/// which is most of the assembly and is not enumerable by inspection. Do NOT try to enlist the readers.</para>
///
/// <para>Membership rule: a class that ASSIGNS <c>GuiTheme.Default</c> must be here, and must still restore the
/// previous value in a <c>finally</c>. <c>PatchNotesThemeTests</c> is also here despite only reading, because it
/// asserts the ambient default's colors directly and is the class the flake was reported on. Other ambient
/// readers stay out of it and are covered anyway by the paragraph above.</para>
/// </summary>
[CollectionDefinition("gui-theme-global", DisableParallelization = true)]
public sealed class GuiThemeGlobalCollection { }
