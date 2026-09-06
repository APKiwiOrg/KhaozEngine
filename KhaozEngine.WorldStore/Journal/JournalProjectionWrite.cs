using System;

namespace KhaozEngine.WorldStore.Journal;

public sealed class JournalProjectionWrite
{
    private readonly byte[] data;
    private readonly byte[] dataChecksum;

    public JournalProjectionWrite(string streamKey, string sectionName, string projectionSchema, int projectionSchemaVersion, byte[] data)
    {
        StreamKey = JournalValidation.StreamKey(streamKey);
        SectionName = JournalValidation.Identity(sectionName, nameof(sectionName), JournalLimits.EngineMaximumIdentityCharacters);
        ProjectionSchema = JournalValidation.Identity(projectionSchema, nameof(projectionSchema), JournalLimits.EngineMaximumIdentityCharacters);
        JournalValidation.Positive(projectionSchemaVersion, nameof(projectionSchemaVersion));
        ProjectionSchemaVersion = projectionSchemaVersion;
        this.data = JournalValidation.CopyBytes(data, JournalLimits.EngineMaximumProjectionSectionBytes, nameof(data));
        dataChecksum = JournalValidation.Hash(this.data);
    }

    public string StreamKey { get; }
    public string SectionName { get; }
    public string ProjectionSchema { get; }
    public int ProjectionSchemaVersion { get; }
    public ReadOnlyMemory<byte> Data => data;
    public ReadOnlyMemory<byte> DataChecksum => dataChecksum;
    public int OwnedByteCount => data.Length;

    public void Validate(JournalLimits? limits = null)
    {
        limits ??= JournalLimits.Maximum;
        JournalValidation.Maximum(StreamKey.Length, limits.StreamKeyCharacters, nameof(StreamKey));
        JournalValidation.Maximum(SectionName.Length, limits.IdentityCharacters, nameof(SectionName));
        JournalValidation.Maximum(ProjectionSchema.Length, limits.IdentityCharacters, nameof(ProjectionSchema));
        JournalValidation.Maximum(data.Length, limits.ProjectionSectionBytes, nameof(Data));
    }
}
