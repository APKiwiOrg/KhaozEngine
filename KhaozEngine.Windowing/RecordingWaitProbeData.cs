using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing;

public enum RecordingWaitRole { Prefix, Main }
public enum RecordingWaitObservationStatus { Missing, Pending, Available, NotApplicable, Unsupported, Unavailable, Fault, IdentityMismatch, InvalidReceipt, Duplicate, Late }
public enum RecordingWaitProbeStatus { Off, Valid, NotRendered, IncompleteCallback, MissingDeclaration, DuplicateDeclaration, MissingRole, DuplicateRole, IdentityMismatch, Unavailable, Fault, InvalidReceipt, AcknowledgementCardinality, PairingMismatch, ModeChanged }
public enum RecordingWaitBoundaryStatus { Off, Available, Unsupported, Unavailable, Contended, Fault }
public enum RecordingWaitReconciliationStatus { Off, Available, MissingBoundary, InvalidRoles, IdentityMismatch, InvalidTotals, NegativeRemainder }

public readonly record struct RecordingWaitProbeRequest(Guid Epoch, long Sequence, bool Enabled, ShadowStageProbeMode Mode = ShadowStageProbeMode.Counts);
public readonly record struct RecordingWaitObservation(RecordingWaitObservationStatus Status, ulong PriorAttempt, GpuRecordingWaitReceipt? Receipt);
public readonly record struct RecordingWaitCallbackResult(RecordingWaitProbeRequest Request, int DeclarationCount,
    bool PrefixExpected, int AcknowledgementCount, int PrefixObservations, int MainObservations,
    RecordingWaitObservation Prefix, RecordingWaitObservation Main, bool IdentityViolation);
public readonly record struct RecordingWaitBoundary(RecordingWaitBoundaryStatus Status, GpuRingWaitTotals? Totals);
public readonly record struct RecordingWaitContribution(long Count, long Ticks);
public readonly record struct RecordingWaitReconciliation(RecordingWaitReconciliationStatus Status, long? Count, long? Ticks);
internal readonly record struct RecordingWaitProbeRow(RecordingWaitProbeRequest Request, RecordingWaitProbeStatus Status,
    RecordingWaitCallbackResult Callback, RecordingWaitBoundary Start, RecordingWaitBoundary End, RecordingWaitReconciliation Reconciliation);

// Tickets are stack-local identity checks. None of these references enter the callback buffer.
public readonly record struct RecordingWaitObservationTicket(RecordingWaitProbeRequest Request, RecordingWaitRole Role,
    IGpuDevice? Device, IGpuCommandList? List, Guid DeviceIdentity, Guid ListIdentity, ulong PriorAttempt, bool Valid);
