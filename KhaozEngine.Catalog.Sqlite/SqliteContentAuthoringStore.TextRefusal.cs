using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Catalog.Sqlite;

public sealed partial class SqliteContentAuthoringStore
{
    static void RequireTextClosePresent(ContentTextRevision close, bool present)
    {
        if (!present)
        {
            throw new ContentAuthoringException(ContentAuthoringRefusal.TextCloseRevisionMissing,
                DescribeMismatch("a text close names a revision this store does not hold live"),
                default, 0, ContentAuthoringException.TextStateMismatchReason)
            {
                TextRowType = close.Type,
                TextRowId = close.DefinitionId,
                FieldName = close.FieldName,
                Language = close.Language,
                FromVersion = close.ValidFromVersion,
            };
        }
    }

    // Inject the backend's close lookup result without reopening immutable plan storage.
    internal static void RefuseMissingTextCloseForTests(ContentTextRevision close, bool present)
        => RequireTextClosePresent(close, present);

    internal static ContentKey RequireTextPlanRowForTests(ContentPublishPlan plan, ContentTextRevision revision)
        => KeyOf(plan, revision);
}
