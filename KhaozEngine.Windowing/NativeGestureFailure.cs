using System;

namespace KhaozEngine.Windowing;

/// <summary>The native gesture operation that first failed.</summary>
public enum NativeGestureFailureStage { Install, Remove, Release, Callback }

/// <summary>First native gesture failure retained for developer diagnostics. No automatic logging occurs.</summary>
public sealed record NativeGestureFailure(NativeGestureFailureStage Stage, string Message, Exception? Exception = null);
