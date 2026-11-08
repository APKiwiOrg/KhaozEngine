using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    CoverageCarryBatch? _coverageBatch;
    CoverageSubmissionParticipant? _coverageParticipant;
    GpuFrameSubmission? _coverageSubmission;
    IGpuFence? _coverageCompletion;
    bool _coverageRegistered, _coverageEncoded;
    long _coverageSerial;

    // Exactly one participant and completion consumer regardless of actor population.
    sealed class CoverageSubmissionParticipant(Scene3D scene) : IGpuFrameSubmissionParticipant
    {
        public void Submitted() => scene.CommitCoverageSubmission();
        public void Abandoned() => scene.AbandonCoverageSubmission();
    }

    void ReserveCoverageStorage(SkinnedHostCoverage owner)
    {
        _coverageBatch ??= new(_gd, _retired);
        _coverageBatch.ReserveOwner(owner);
        owner.Request.History.Reserve(_coverageBatch.SlotsPerActor);
    }

    void BeginCoverageFrame(GpuFrameSubmission? submission)
    {
        if (_coverageRegistered) throw StaleCoverage(null);
        _coverageBatch?.PollCompleted();
        _coverageBatch?.AbandonRecording();
        _coverageSubmission = submission; _coverageCompletion = null; _coverageEncoded = false;
        _coverageDraws.Clear(); _coverageInstances.Clear(); _coverageMaterials.Clear();
        foreach (var owner in _coverageHosts)
        {
            var p = owner.Request;
            p.Raw = p.Published = p.OrdinaryEncoded = p.TokenFree = false;
            p.OutputSlot = p.PreviousSlot = -1;
            p.PublicationGeneration = 0; p.Failure = default;
            Array.Clear(p.Draws); Array.Clear(p.Outlines);
        }
    }

    void CommitCoverageSubmission()
    {
        if (!_coverageRegistered) return;
        if (_coverageEncoded)
        {
            _coverageBatch!.CommitSubmitted(_coverageCompletion);
            long serial = ++_coverageSerial;
            foreach (var owner in _coverageHosts)
            {
                var p = owner.Request;
                if (p.Published) p.History.Commit(p, p.View!, serial);
                else if (p.OrdinaryEncoded)
                {
                    _coverageBatch.Discontinue(owner, _coverageCompletion);
                    p.History.Discontinue(owner.Actor, serial);
                }
            }
        }
        else _coverageBatch?.AbandonRecording();
        _coverageRegistered = _coverageEncoded = false;
        _coverageCompletion = null;
    }

    void AbandonCoverageSubmission()
    {
        _coverageBatch?.AbandonRecording(_coverageCompletion);
        _coverageRegistered = _coverageEncoded = false;
        _coverageCompletion = null;
    }

    /// <summary>The most recent preparation/publication refusal for this actor in the current frame.</summary>
    public CoverageFailure CoverageFailureFor(MotionKey actor)
    {
        RequireCoverageThread();
        ObjectDisposedException.ThrowIf(_disposalComplete, this);
        foreach (var owner in _coverageHosts) if (owner.Actor == actor) return owner.Request.Failure;
        return new(CoverageFailureCode.IdentityMismatch, null, 0, 0, 0, 0);
    }

    internal CoverageHistorySnapshot CoverageHistoryForTests(MotionKey actor)
    {
        foreach (var owner in _coverageHosts) if (owner.Actor == actor) return owner.Request.History.Committed;
        return default;
    }
}
