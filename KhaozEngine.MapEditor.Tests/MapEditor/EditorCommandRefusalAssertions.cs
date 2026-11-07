using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

internal static class EditorCommandRefusalAssertions
{
    internal static MapEditorCommandException Throws(Action operation, MapDocumentRefusal reason, string kind, string id)
    {
        var error = Assert.Throws<MapEditorCommandException>(operation);
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(kind, error.SubjectKind);
        Assert.Equal(id, error.EntityId);
        return error;
    }
}
