using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// <see cref="CatalogAdminActions.RegisterReads"/>, the door a bundle-derived catalog registers so its console
/// serves reads and exposes no publish, import, pin or edit
/// (https://github.com/APKiwiOrg/KhaozEngine/issues/1132).
/// <para>
/// The five names are asserted literally, and every one of them is dispatched over a store that refuses and
/// records every write, so a read that grew a write would fail here rather than on a hosted store.
/// </para>
/// </summary>
public sealed class CatalogReadRegistrationTests : IDisposable
{
    static readonly string[] TheFive =
    [
        "catalog-draft",
        "catalog-get",
        "catalog-list",
        "catalog-schema",
        "catalog-versions",
    ];

    readonly CatalogActionHarness _harness = new();

    /// <summary>
    /// A fresh surface holds EXACTLY the five reads, none declared mutating, and every other name of the
    /// declared sixteen is absent. A seventeenth action added to the read path makes the surface six long.
    /// </summary>
    [Fact]
    public void RegisterReads_LeavesExactlyTheFiveReadNames()
    {
        ServerAdmin reads = CatalogActionHarness.NewSurface();
        CatalogAdminActions.RegisterReads(reads, _harness.Store, _harness.Registry);

        string[] registered = reads.ActionNames.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(TheFive, registered);
        Assert.All(registered, name => Assert.False(
            reads.IsMutatingAction(name), name + " is a read and was registered mutating."));

        string[] others = CatalogAdminActions.ActionNames.Except(TheFive).ToArray();
        Assert.Equal(11, others.Length);
        Assert.All(others, name => Assert.False(
            reads.TryGetAction(name, out _), name + " is not a read and the read door registered it."));
    }

    /// <summary>
    /// Each of the five answers 200 with the content it reads, over a store whose every write member records
    /// and throws. The draft is open and the version is pinned, so the draft and versions reads meet the state
    /// they would most plausibly be tempted to touch.
    /// </summary>
    [Fact]
    public async Task EveryRead_AnswersOverAStoreThatRefusesEveryWrite()
    {
        int version = await _harness.PublishThingsAsync("stone_sword");
        await _harness.Store.ApplyEditsAsync(
            [ContentEdit.Add(CatalogActionHarness.Thing, new ContentKey("iron_sword"), CatalogActionHarness.Fields())],
            CatalogAdminActions.Actor,
            CatalogActionHarness.Operator,
            "pending");
        await _harness.Store.SetPinnedVersionAsync(version, CatalogAdminActions.Actor, CatalogActionHarness.Operator);

        var store = new WriteRefusingAuthoringStore(_harness.Store);
        ServerAdmin reads = CatalogActionHarness.NewSurface();
        CatalogAdminActions.RegisterReads(reads, store, _harness.Registry);

        JsonElement schema = await OkAsync(reads, CatalogAdminActions.SchemaAction, null);
        Assert.Equal(2, schema.GetProperty("types").GetArrayLength());

        JsonElement list = await OkAsync(
            reads, CatalogAdminActions.ListAction, """{ "typeKey": "thing" }""");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal("stone_sword", list.GetProperty("rows")[0].GetProperty("key").GetString());

        JsonElement get = await OkAsync(
            reads,
            CatalogAdminActions.GetAction,
            """{ "typeKey": "thing", "key": "stone_sword", "includeAudit": true }""");
        Assert.Equal("stone_sword", get.GetProperty("key").GetString());
        Assert.Equal(1, get.GetProperty("history").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, get.GetProperty("audit").ValueKind);

        JsonElement draft = await OkAsync(reads, CatalogAdminActions.DraftAction, null);
        Assert.Equal(version, draft.GetProperty("draft").GetProperty("baseVersion").GetInt32());
        Assert.Equal("iron_sword", draft.GetProperty("edits")[0].GetProperty("key").GetString());

        JsonElement versions = await OkAsync(reads, CatalogAdminActions.VersionsAction, null);
        Assert.Equal(version, versions.GetProperty("activeVersion").GetInt32());
        Assert.Equal(version, versions.GetProperty("pinnedVersion").GetInt32());
        Assert.Equal(1, versions.GetProperty("versions").GetArrayLength());

        Assert.Empty(store.WriteAttempts);

        // The double is armed: a write through it is refused and recorded, so the empty record above means
        // no read reached one rather than that nothing could be recorded.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SetPinnedVersionAsync(null, CatalogAdminActions.Actor, CatalogActionHarness.Operator));
        Assert.Equal("SetPinnedVersionAsync", Assert.Single(store.WriteAttempts));
    }

    /// <summary>
    /// <c>Register</c> on a surface that already holds the reads is refused with the SAME exception a second
    /// <c>Register</c> throws, and the refusal leaves no write action behind.
    /// </summary>
    [Fact]
    public void RegisterAfterRegisterReads_IsRefusedAsASecondRegisterIs()
    {
        ArgumentException twice = Assert.Throws<ArgumentException>(
            () => CatalogAdminActions.Register(_harness.Admin, _harness.Store, _harness.Registry));

        ServerAdmin reads = CatalogActionHarness.NewSurface();
        CatalogAdminActions.RegisterReads(reads, _harness.Store, _harness.Registry);
        ArgumentException after = Assert.Throws<ArgumentException>(
            () => CatalogAdminActions.Register(reads, _harness.Store, _harness.Registry));

        Assert.Equal(twice.GetType(), after.GetType());
        Assert.Equal(twice.ParamName, after.ParamName);
        Assert.Equal(twice.Message, after.Message);
        Assert.Equal(TheFive, reads.ActionNames.OrderBy(static name => name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Each required argument is checked before anything is registered.</summary>
    [Fact]
    public void RegisterReads_RefusesANullArgument()
    {
        ServerAdmin reads = CatalogActionHarness.NewSurface();

        Assert.Throws<ArgumentNullException>(
            () => CatalogAdminActions.RegisterReads(null!, _harness.Store, _harness.Registry));
        Assert.Throws<ArgumentNullException>(
            () => CatalogAdminActions.RegisterReads(reads, null!, _harness.Registry));
        Assert.Throws<ArgumentNullException>(
            () => CatalogAdminActions.RegisterReads(reads, _harness.Store, null!));
        Assert.Empty(reads.ActionNames);
    }

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();

    static async Task<JsonElement> OkAsync(ServerAdmin admin, string action, string? body)
    {
        AdminActionResult result = await CatalogActionHarness.DispatchAsync(admin, action, body);
        Assert.Equal(AdminActionStatus.Ok, result.Status);
        return CatalogActionHarness.Wire(result);
    }
}
