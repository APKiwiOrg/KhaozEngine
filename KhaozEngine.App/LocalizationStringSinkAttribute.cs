using System;

namespace KhaozEngine.App;

/// <summary>
/// Marks a method or constructor as a discouraged raw-<see cref="string"/> player-facing sink. The
/// localization analyzer (KELOC001) flags CALLERS of any member carrying this attribute, so a sink a game
/// marks itself is caught without hard-coding method names. The engine's own Gui sinks take only
/// <see cref="LocalizedText"/> and carry no raw-string overload.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, Inherited = false, AllowMultiple = false)]
public sealed class LocalizationStringSinkAttribute : Attribute
{
}
