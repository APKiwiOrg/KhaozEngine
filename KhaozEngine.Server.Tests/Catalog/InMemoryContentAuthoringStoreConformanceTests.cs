using System;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The conformance suite against <see cref="InMemoryContentAuthoringStore"/>, which is the REFERENCE
/// behaviour rather than one more backend: it is the store the publish pipeline, the draft suite and the
/// bundle suite are all written against, so a fact it fails is a defect in the reference.
/// <para>
/// It still writes a real pack to a temp directory, because a publish writes files before it writes rows and
/// a store with no pack target cannot publish at all.
/// </para>
/// </summary>
public sealed class InMemoryContentAuthoringStoreConformanceTests : ContentAuthoringStoreConformance, IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "kec-conformance-" + Guid.NewGuid().ToString("n"));
    int _packs;
    bool _auditFaultArmed;

    /// <inheritdoc />
    protected override IContentAuthoringStore NewStore()
        => new InMemoryContentAuthoringStore(Registry, NewPack(), ReadTheClock);

    /// <inheritdoc />
    /// <remarks>
    /// Nothing to empty: an in-memory store IS its database, so a fresh one is an empty one. It gets its own
    /// pack directory too, so an import cannot read a file the source store wrote.
    /// </remarks>
    protected override Task<IContentAuthoringStore> ResetToEmptyAsync() => OpenAsync();

    /// <inheritdoc />
    /// <remarks>
    /// <b>Through the CLOCK, which is the one seam the audit write cannot avoid.</b> Every audit entry is
    /// stamped from the store's own clock, so a clock that throws is an audit insert that fails, and it needs
    /// no test-only member on the store. The draft half reads the clock too, when it OPENS a draft, so fact 17
    /// arms the fault against a draft that is already open and the only clock read left is the audit's.
    /// </remarks>
    protected override IDisposable ArmAnAuditWriteFault(IContentAuthoringStore store)
    {
        _auditFaultArmed = true;
        return new Disarm(this);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every member of this store takes one gate, so a look cannot land INSIDE the commit. It can still
    /// straddle it, between the two reads below, which is what the second read of the pointer catches: a look
    /// whose pointer moved under it is discarded rather than reported as a torn one.
    /// </remarks>
    protected override async Task<CatalogPointerLook?> LookAsync(IContentAuthoringStore store, int versionNumber)
    {
        ArgumentNullException.ThrowIfNull(store);
        int before = await store.GetActiveVersionAsync();
        bool present = await store.GetVersionAsync(versionNumber) is not null;
        int after = await store.GetActiveVersionAsync();
        return before == after ? new CatalogPointerLook(before, present) : null;
    }

    /// <inheritdoc />
    protected override IPackStore PackOf(IContentAuthoringStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store is InMemoryContentAuthoringStore { PackStore: IPackStore pack }
            ? pack
            : throw new InvalidOperationException(
                "This subclass opens every store over a pack directory of its own, so one without a pack target did not come from here.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }
    }

    DateTimeOffset ReadTheClock()
        => _auditFaultArmed
            ? throw new InvalidOperationException("The audit write failed, which is fact 17's injected fault.")
            : DateTimeOffset.UtcNow;

    FileSystemPackStore NewPack()
    {
        _packs++;
        return new FileSystemPackStore(Path.Combine(_root, "pack-" + _packs.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    sealed class Disarm(InMemoryContentAuthoringStoreConformanceTests owner) : IDisposable
    {
        public void Dispose() => owner._auditFaultArmed = false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Overridden because an in-memory store is CREATED with its schema: there is no empty database for a
    /// mode to decide about, so both modes succeed and the version it reports is a constant. The assertion is
    /// kept rather than dropped, because the constant is what a provider is compared against.
    /// </remarks>
    public override async Task Fact01_AutoCreateOnAnEmptyStoreCreatesTheSchemaAndReportsTheCurrentVersion()
    {
        IContentAuthoringStore store = NewStore();
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        Assert.Equal(InMemoryContentAuthoringStore.SchemaVersion, await store.GetSchemaVersionAsync());
        Assert.Equal(0, await store.GetActiveVersionAsync());
    }

    /// <inheritdoc />
    /// <remarks>
    /// Overridden as TRIVIALLY SATISFIED. The refusal is about a database that exists and carries no schema,
    /// and an in-memory store has no such state to be in: it is constructed with its tables already there.
    /// What is asserted instead is that the mode is accepted rather than rejected, so a caller written against
    /// the seam can hand either mode to any backend.
    /// </remarks>
    public override async Task Fact02_ValidateOnlyOnAnEmptyDatabaseRefusesAndNamesTheMigration()
    {
        IContentAuthoringStore store = NewStore();

        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

        Assert.Equal(InMemoryContentAuthoringStore.SchemaVersion, await store.GetSchemaVersionAsync());
    }

    /// <inheritdoc />
    /// <remarks>Overridden for the same reason as fact 2, and here the base assertion happens to hold anyway.</remarks>
    public override async Task Fact03_ValidateOnlyOnACorrectSchemaSucceeds()
    {
        IContentAuthoringStore store = await OpenAsync();

        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

        Assert.Equal(InMemoryContentAuthoringStore.SchemaVersion, await store.GetSchemaVersionAsync());
    }
}
