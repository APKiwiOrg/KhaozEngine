using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Facts 1 to 3, the SCHEMA half: what each mode does to a database, which is the one group whose whole
/// subject is the database rather than the seam.
/// <para>
/// The property behind all three is one sentence. A production host sets
/// <see cref="ContentAuthoringSchemaMode.ValidateOnly"/> so a connection string pointing at the wrong
/// database cannot silently create a second, empty catalog and serve it, and a refusal is only useful when it
/// names the object and the migration an operator has to apply.
/// </para>
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>
    /// FACT 1. <see cref="ContentAuthoringSchemaMode.AutoCreate"/> on an empty database creates the schema and
    /// reports version 1, which is the number a migration compares against.
    /// </summary>
    [Fact]
    public virtual async Task Fact01_AutoCreateOnAnEmptyStoreCreatesTheSchemaAndReportsVersionOne()
    {
        IContentAuthoringStore store = NewStore();

        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        Assert.Equal(1, await store.GetSchemaVersionAsync());
        Assert.Equal(0, await store.GetActiveVersionAsync());
        Assert.Null(await store.GetPinnedVersionAsync());

        // The epoch is minted once at creation. It is what makes "version 12" answerable across two databases
        // that share no history, so a store that reported an empty one would have no identity at all.
        Assert.NotEmpty(await store.GetStoreEpochAsync());
    }

    /// <summary>
    /// FACT 2. <see cref="ContentAuthoringSchemaMode.ValidateOnly"/> on an empty database throws, NAMING the
    /// required migration. A refusal an operator cannot act on is a crash with extra steps.
    /// </summary>
    [Fact]
    public virtual async Task Fact02_ValidateOnlyOnAnEmptyDatabaseRefusesAndNamesTheMigration()
    {
        IContentAuthoringStore store = NewStore();

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

        Assert.Equal(ContentAuthoringException.SchemaMismatchReason, refused.Reason);
        Assert.Contains(RequiredMigration, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// FACT 3. <see cref="ContentAuthoringSchemaMode.ValidateOnly"/> against a schema this build created
    /// succeeds. The pair with fact 2 is the whole of the mode: it refuses what is absent and accepts what is
    /// right, and a validator that could only do the first would be unusable in production.
    /// </summary>
    [Fact]
    public virtual async Task Fact03_ValidateOnlyOnACorrectSchemaSucceeds()
    {
        IContentAuthoringStore store = await OpenAsync();

        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

        Assert.Equal(1, await store.GetSchemaVersionAsync());
    }
}
