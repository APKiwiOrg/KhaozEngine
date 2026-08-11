using KhaozEngine.Showcase;

// The native Metal backend is opt-in and never self-registers (see KhaozEngineMetal's own doc), so the
// showcase, as the engine's windowed testbed, makes the one explicit call a consuming app makes at startup.
// The OS probe still selects the incumbent by default: this only makes KE_GRAPHICS_BACKEND=metal-native
// honourable, which is what rollout gate 5's windowed pass boots with.
if (KhaozEngine.Gpu.Metal.KhaozEngineMetal.IsPlatformSupported)
    KhaozEngine.Gpu.Metal.KhaozEngineMetal.Register();

using var app = new ShowcaseApp();
app.Run();
return 0;
