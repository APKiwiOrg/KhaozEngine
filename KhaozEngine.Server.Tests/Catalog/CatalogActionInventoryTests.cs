using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The action COUNT, which is a number the spec states once and means.
/// <para>
/// Earlier spec drafts counted eleven in one place and fourteen in another, by collapsing pairs a caller
/// still has to know the names of, so this test is what stops the count drifting again. It asserts the
/// sixteen names literally rather than counting them, because a rename is as bad as a loss: a console binds
/// to the NAME, and a typo is a 404 at the one moment an operator is trying to look at content.
/// </para>
/// </summary>
public sealed class CatalogActionInventoryTests : IDisposable
{
    static readonly string[] TheSixteen =
    [
        "catalog-diff",
        "catalog-discard",
        "catalog-draft",
        "catalog-edit",
        "catalog-export",
        "catalog-get",
        "catalog-import",
        "catalog-list",
        "catalog-pin",
        "catalog-publish",
        "catalog-rollback",
        "catalog-schema",
        "catalog-sweep",
        "catalog-validate",
        "catalog-verify",
        "catalog-versions",
    ];

    readonly CatalogActionHarness _harness = new();

    /// <summary>Registering leaves EXACTLY sixteen names, spelled exactly as the spec spells them.</summary>
    [Fact]
    public void Register_LeavesExactlyTheSixteenNames()
    {
        string[] registered = _harness.Admin.ActionNames
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(16, registered.Length);
        Assert.Equal(TheSixteen, registered);
    }

    /// <summary>
    /// The public name list and the registration agree. A console reads the constants, and a name that is
    /// declared but never registered is a 404 a caller only finds at runtime.
    /// </summary>
    [Fact]
    public void TheDeclaredNames_AreTheRegisteredOnes()
    {
        Assert.Equal(
            TheSixteen,
            CatalogAdminActions.ActionNames.OrderBy(static name => name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// No content action answers anything other than 200, 400 or 409. There is no 501 arm on this path at
    /// all: the 501s belong to four BUILT-IN routes gated on capability flags, and a registered action that
    /// does not exist is a 404 from the lookup.
    /// </summary>
    [Fact]
    public async Task EveryAction_AnswersOneOfTheThreeStatusesAndNever202()
    {
        await _harness.PublishThingsAsync("stone_sword");

        var seen = new List<AdminActionStatus>();
        foreach (string name in CatalogAdminActions.ActionNames)
        {
            AdminActionResult result = await _harness.CallAsync(name, null);
            seen.Add(result.Status);
        }

        Assert.All(seen, status => Assert.True(
            status is AdminActionStatus.Ok or AdminActionStatus.BadRequest or AdminActionStatus.Conflict,
            "a catalog action answered " + status + ", and the three it may answer are Ok, BadRequest and Conflict."));
        Assert.DoesNotContain(AdminActionStatus.Accepted, seen);
    }

    /// <summary>A name nothing registered is a lookup MISS, which the endpoint answers 404 to.</summary>
    [Fact]
    public void AnUnregisteredName_IsALookupMiss()
        => Assert.False(_harness.Admin.TryGetAction("catalog-nope", out _));

    /// <summary>
    /// Registering twice throws, which is the <see cref="ServerAdmin"/> duplicate-name rule reaching the
    /// catalog helper. A game that calls it twice has a startup defect rather than a silently half
    /// registered surface.
    /// </summary>
    [Fact]
    public void RegisteringTwice_Throws()
        => Assert.Throws<ArgumentException>(
            () => CatalogAdminActions.Register(_harness.Admin, _harness.Store, _harness.Registry));

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();
}
