using System.Numerics;
using KhaozEngine.App;

namespace KhaozEngine.Gui;

/// <summary>
/// One caller-ordered part of a composed label. <see cref="Content"/> remains localizable and
/// <see cref="Color"/> optionally overrides the owning control's label color.
/// </summary>
public readonly record struct LabelSegment(LocalizedText Content, Vector4? Color = null);
