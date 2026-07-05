using System;

namespace KhaozEngine.App;

/// <summary>
/// Marks a method or constructor as a discouraged raw-<see cref="string"/> player-facing sink. The
/// localization analyzer (KELOC001) flags CALLERS of any member carrying this attribute, so the engine's
/// obsolete string overloads - and any sink a game marks itself - are caught without hard-coding method names.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, Inherited = false, AllowMultiple = false)]
public sealed class LocalizationStringSinkAttribute : Attribute
{
}
