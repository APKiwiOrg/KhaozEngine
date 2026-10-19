using System;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Bounded retained workspaces for nested queries inside one exclusive reader interval.</summary>
internal struct CapsuleSweepScratchSlot
{
    internal const int MaximumDepth = 4;
    CapsuleSweepScratch?[]? _workspaces;
    int _depth;

    internal CapsuleSweepScratch? Idle =>
        _workspaces is null || _depth == MaximumDepth ? null : _workspaces[_depth];

    /// <summary>Loans the next inactive depth, or refuses without allocating at the nesting bound.</summary>
    internal CapsuleSweepScratch? Take()
    {
        if (_depth == MaximumDepth) return null;
        _workspaces ??= new CapsuleSweepScratch?[MaximumDepth];
        CapsuleSweepScratch scratch = _workspaces[_depth] ??= new();
        _depth++;
        return scratch;
    }

    internal void Restore(CapsuleSweepScratch scratch)
    {
        if (_depth == 0 || !ReferenceEquals(_workspaces![_depth - 1], scratch))
            throw new InvalidOperationException("Certified sweep scratch loans must restore in reverse order.");
        _depth--;
    }
}
