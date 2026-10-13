using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

/// <summary>Reusable component geometry and text, shared by measurement and drawing on each thread.</summary>
internal sealed class NameplateResolution
{
    [ThreadStatic]
    private static NameplateResolution? _shared;

    private NameplateResolvedComponent[] _components = Array.Empty<NameplateResolvedComponent>();
    private int _count;

    internal static NameplateResolution Shared => _shared ??= new NameplateResolution();

    internal ReadOnlySpan<NameplateResolvedComponent> Components => _components.AsSpan(0, _count);
    internal ref NameplateResolvedComponent this[int index] => ref _components[index];
    internal Rect Bounds { get; set; }
    internal float ContentWidth { get; set; }

    internal void Prepare(int count)
    {
        if (_components.Length < count)
            Array.Resize(ref _components, count);

        Array.Clear(_components);
        _count = count;
        for (int i = 0; i < count; i++)
            _components[i].Text = string.Empty;

        Bounds = default;
        ContentWidth = 0f;
    }
}

/// <summary>A component's draw state, local rect and final text after any ellipsis.</summary>
internal struct NameplateResolvedComponent
{
    public bool Drawn;
    public Rect Rect;
    public string Text;
}
