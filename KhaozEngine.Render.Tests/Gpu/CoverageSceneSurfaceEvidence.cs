using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

internal sealed record CoverageSceneSurfaceEvidence(ulong FrameId, ulong ViewId, ulong Actor, ulong PreparationId,
    CoveragePresentation StagedPresentation, CoverageGpuOutcome? CompletedOutcome, CoverageGpuReservation Reservation,
    int CurrentBufferId, int PreviousBufferId, int StaticBufferId, string Selection, string Signature,
    uint[] HeaderWords, uint[] SourceWords, uint[] FinalWords, CoverageSceneConsumer[] Consumers)
{
    public CoverageFrameOutcome Admission { get; init; }
    public string SubmittedBufferRole { get; init; } = "none";
    public string AdmissionPresentation => Admission.Presentation.ToString();
    public string AdmissionFailure => Admission.Failure.Code.ToString();
    public string? CompletedPresentation => CompletedOutcome?.Outcome.Presentation.ToString();
    public string? CompletedFailure => CompletedOutcome?.Outcome.Failure.Code.ToString();
}

internal sealed record CoverageSceneConsumer(string Pass, int CurrentBufferId, int PreviousBufferId, int StaticBufferId,
    uint CurrentBase, uint PreviousBase, uint StaticBase, int VertexCount, int TriangleCount,
    int FirstCorner, int CornerCount, uint InstanceSlot, ulong Actor, bool VisibleMain, string ShadowKind);
