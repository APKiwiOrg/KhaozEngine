namespace KhaozEngine.Attachments;

// Each family owns its copy so source enrichment preserves every identifier and the original error.
internal interface IAttachmentDataException
{
    MalformedDataException WithSource(string sourceName);
}
