using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing;

public sealed partial class Frame
{
    RecordingWaitProbeRequest _recordingWaitRequest;
    RecordingWaitObservation _recordingWaitPrefix, _recordingWaitMain;
    int _recordingWaitDeclarations, _recordingWaitAcknowledgements, _recordingWaitPrefixes, _recordingWaitMains;
    bool _recordingWaitPrefixExpected, _recordingWaitIdentityViolation;

    public void DeclareRecordingWaitPrefix(bool expected)
    {
        if (!_recordingWaitRequest.Enabled) return;
        if (_recordingWaitAcknowledgements != 0) _recordingWaitIdentityViolation = true;
        if (++_recordingWaitDeclarations != 1) return;
        _recordingWaitPrefixExpected = expected;
        if (!expected && _recordingWaitPrefixes == 0)
            _recordingWaitPrefix = new(RecordingWaitObservationStatus.NotApplicable, 0, null);
    }

    public RecordingWaitObservationTicket BeginRecordingWaitObservation(RecordingWaitRole role, IGpuDevice device, IGpuCommandList list)
    {
        if (!_recordingWaitRequest.Enabled) return default;
        if (role is not (RecordingWaitRole.Prefix or RecordingWaitRole.Main))
        { _recordingWaitIdentityViolation = true; return default; }
        ref int count = ref (role == RecordingWaitRole.Prefix ? ref _recordingWaitPrefixes : ref _recordingWaitMains);
        if (_recordingWaitAcknowledgements != 0) _recordingWaitIdentityViolation = true;
        if (++count != 1) return default;
        ref RecordingWaitObservation observation = ref Observation(role);
        observation = new(RecordingWaitObservationStatus.Pending, 0, null);
        try
        {
            if (device is not IGpuRingWaitSource nativeDevice || list is not IGpuRecordingWaitSource nativeList)
            { observation = new(RecordingWaitObservationStatus.Unsupported, 0, null); return new(_recordingWaitRequest, role, device, list, default, default, 0, false); }
            if (!nativeList.TryGetRecordingAttempt(out ulong prior))
            { observation = new(RecordingWaitObservationStatus.Unavailable, 0, null); return new(_recordingWaitRequest, role, device, list, default, default, 0, false); }
            Guid deviceId = nativeDevice.RingWaitDeviceIdentity, listId = nativeList.RecordingWaitListIdentity;
            if (deviceId == Guid.Empty || listId == Guid.Empty)
            { observation = new(RecordingWaitObservationStatus.Unavailable, prior, null); return new(_recordingWaitRequest, role, device, list, default, default, 0, false); }
            observation = new(RecordingWaitObservationStatus.Pending, prior, null);
            return new(_recordingWaitRequest, role, device, list, deviceId, listId, prior, true);
        }
        catch
        { observation = new(RecordingWaitObservationStatus.Fault, 0, null); return new(_recordingWaitRequest, role, device, list, default, default, 0, false); }
    }

    public void CompleteRecordingWaitObservation(in RecordingWaitObservationTicket ticket)
    {
        if (!_recordingWaitRequest.Enabled) return;
        if (ticket.Request != _recordingWaitRequest || _recordingWaitAcknowledgements != 0
            || ticket.Role is not (RecordingWaitRole.Prefix or RecordingWaitRole.Main))
        { _recordingWaitIdentityViolation = true; return; }
        if (!ticket.Valid) return;
        ref RecordingWaitObservation observation = ref Observation(ticket.Role);
        if (observation.Status != RecordingWaitObservationStatus.Pending)
        {
            if (ticket.Role == RecordingWaitRole.Prefix) _recordingWaitPrefixes++;
            else _recordingWaitMains++;
            return;
        }
        try
        {
            if (ticket.Device is not IGpuRingWaitSource device || ticket.List is not IGpuRecordingWaitSource list
                || device.RingWaitDeviceIdentity != ticket.DeviceIdentity || list.RecordingWaitListIdentity != ticket.ListIdentity)
            { observation = observation with { Status = RecordingWaitObservationStatus.IdentityMismatch }; return; }
            if (!list.TryGetRecordingAttempt(out ulong attempt) || !list.TryGetRecordingWait(out var receipt))
            { observation = observation with { Status = RecordingWaitObservationStatus.Unavailable }; return; }
            if (attempt != unchecked(ticket.PriorAttempt + 1) || receipt.Attempt != attempt
                || receipt.DeviceIdentity != ticket.DeviceIdentity || receipt.ListIdentity != ticket.ListIdentity)
            { observation = observation with { Status = RecordingWaitObservationStatus.IdentityMismatch }; return; }
            observation = new(RecordingWaitProbeAccounting.ValidReceipt(receipt, ticket.PriorAttempt)
                ? RecordingWaitObservationStatus.Available : RecordingWaitObservationStatus.InvalidReceipt, ticket.PriorAttempt, receipt);
        }
        catch { observation = observation with { Status = RecordingWaitObservationStatus.Fault }; }
    }

    ref RecordingWaitObservation Observation(RecordingWaitRole role) =>
        ref (role == RecordingWaitRole.Prefix ? ref _recordingWaitPrefix : ref _recordingWaitMain);

    internal void BeginRecordingWaitCallback(in RecordingWaitProbeRequest request)
    {
        _recordingWaitRequest = request;
        _recordingWaitPrefix = _recordingWaitMain = default;
        _recordingWaitDeclarations = _recordingWaitAcknowledgements = _recordingWaitPrefixes = _recordingWaitMains = 0;
        _recordingWaitPrefixExpected = _recordingWaitIdentityViolation = false;
    }

    internal void CompleteRecordingWaitCallback()
    { if (_recordingWaitRequest.Enabled) _recordingWaitAcknowledgements++; }

    internal RecordingWaitCallbackResult CompletedRecordingWaitCallback => new(_recordingWaitRequest,
        _recordingWaitDeclarations, _recordingWaitPrefixExpected, _recordingWaitAcknowledgements,
        _recordingWaitPrefixes, _recordingWaitMains, _recordingWaitPrefix, _recordingWaitMain, _recordingWaitIdentityViolation);
}
