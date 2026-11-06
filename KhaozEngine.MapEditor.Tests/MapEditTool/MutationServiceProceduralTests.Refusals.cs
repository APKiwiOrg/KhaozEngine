using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapEditor;

namespace KhaozEngine.Tests.MapEditTool;

public partial class MutationServiceProceduralTests
{
    static MapEditorCommandException AssertProceduralRefusal(Action operation, string kind, string id,
        MapDocumentRefusal reason = MapDocumentRefusal.IdentityDuplicate)
        => EditorCommandRefusalAssertions.Throws(operation, reason, kind, id);
}
