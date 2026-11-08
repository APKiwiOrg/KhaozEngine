namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Producer-owned ordering context for a space diagnostic whose cause may not yet be typed.</summary>
internal sealed record MapSpaceDiagnostic(string SortOwnerId, string Message, MapDocumentFinding? Finding);
