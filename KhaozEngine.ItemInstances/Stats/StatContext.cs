using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// What the SITUATION contributes to one evaluation, spec 11.2: the tags in play and a mask a game
/// condition may read. It carries nothing else, and in particular it does NOT carry the condition
/// registry.
/// <para>
/// <b>The registry arrives by constructor instead.</b> Spec 11.2 declared it here and spec 11.6 declared it
/// on <see cref="ContentStatEvaluator"/>'s constructor, and one dependency with two homes is how two call
/// sites end up holding two registries. Contracts 14.4's shape wins for every injected seam in this design,
/// so the constructor is the one home and this type is pure data.
/// </para>
/// <para>
/// <b><see cref="Tags"/> is half of a scope match and never all of it.</b> A line applies when every tag in
/// its scope is in the UNION of these tags and the target <c>stat</c> row's own tags (spec 11.3), which is
/// what keeps the taxonomy out of the call site: "increased fire resistance" needs no <c>fire</c> here,
/// because the stat row carries it.
/// </para>
/// </summary>
/// <param name="Tags">The tags in play, in any order, compared by membership.</param>
/// <param name="ConditionMask">
/// A game-defined bit mask a registered condition may read. The engine never interprets it, because the
/// engine defines no condition at all in v1.
/// </param>
public readonly record struct StatContext(ReadOnlyMemory<int> Tags, int ConditionMask);
