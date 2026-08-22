using KhaozEngine.Showcase;

// The native Metal backend is opt-in and never self-registers (see KhaozEngineMetal's own doc), so the
// showcase, as the engine's windowed testbed, makes the one explicit call a consuming app makes at startup.
// Since 17.40.0 the OS probe SELECTS that backend on macOS, so this line is what the showcase boots on
// rather than only what makes KE_GRAPHICS_BACKEND=metal-native honourable. Without it the showcase would
// still boot, on the Veldrid incumbent with a WARN, which is what any game that has not taken the package
// gets. KE_GRAPHICS_BACKEND=metal is the way back to the incumbent deliberately.
if (KhaozEngine.Gpu.Metal.KhaozEngineMetal.IsPlatformSupported)
    KhaozEngine.Gpu.Metal.KhaozEngineMetal.Register();

using var app = new ShowcaseApp();
app.Run();
return 0;
