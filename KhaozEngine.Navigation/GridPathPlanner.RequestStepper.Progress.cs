using System;
using System.Numerics;
using System.Threading;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    partial struct RequestStepper
    {
        enum ProgressPurpose { CertificateStart, CertificateNode, SearchStart, PoppedNode }

        void BeginCertificate()
        {
            if (!_planner.CertificateStartsInBounds(_startLayer, _startX, _startZ))
            {
                BeginSearch();
                return;
            }

            _component = _planner._connectivity.ComponentOf(_startId);
            if (_component < 0 || ExceedsCertificateLimits(_planner._connectivity.Members(_component).Length))
                BeginSearch();
            else
                Phase = SearchPhase.CertificateLabel;
        }

        bool ExceedsCertificateLimits(int nodes)
            => ExceedsCertificateNodeLimit(nodes) || _goal.MemberCount <= 0 ||
                ExceedsCertificateComparisonLimit(nodes, _goal.MemberCount);

        void CheckCertificateLabel()
        {
            int node = _scratch.MemberNodeIds[_progressIndex++];
            if (!CertificateMemberIsDisconnected(_planner._connectivity, _component, node))
                BeginSearch();
            else if (_progressIndex == _goal.MemberCount)
                BeginProgress(_startLayer, _startX, _startZ, ProgressPurpose.CertificateStart);
        }

        void BeginProgress(int layer, int x, int z, ProgressPurpose purpose)
        {
            _progressPurpose = purpose;
            _progressFeet = _planner.CellFeet(layer, x, z);
            _progress = double.PositiveInfinity;
            _progressIndex = 0;
            Phase = purpose is ProgressPurpose.CertificateStart or ProgressPurpose.CertificateNode
                ? SearchPhase.CertificateProgress : SearchPhase.SearchProgress;
        }

        void CompareProgress(int operations)
        {
            int members = _goal.MemberCount;
            int end = _progressIndex + Math.Min(operations, members - _progressIndex);
            Vector3 feet = _progressFeet;
            double progress = _progress;
            var goalMembers = _scratch.Members;
            for (int index = _progressIndex; index < end; index++)
                progress = ReduceProgress(progress, feet, goalMembers[index]);
            _progress = progress;
            _progressIndex = end;
            if (end < members) return;

            switch (_progressPurpose)
            {
                case ProgressPurpose.CertificateStart:
                    _startProgress = _progress;
                    FinishCertificateComparison();
                    break;
                case ProgressPurpose.CertificateNode:
                    FinishCertificateComparison();
                    break;
                case ProgressPurpose.SearchStart:
                    _closestProgress = _progress;
                    NextPop();
                    break;
                case ProgressPurpose.PoppedNode:
                    FinishPoppedNode(_progress);
                    break;
            }
        }

        void FinishCertificateComparison()
        {
            if (RejectsCertificate(_progress, _startProgress))
            {
                BeginSearch();
                return;
            }

            ReadOnlySpan<int> component = _planner._connectivity.Members(_component);
            if (_componentIndex < component.Length && component[_componentIndex] == _startId) _componentIndex++;
            if (_componentIndex == component.Length)
            {
                Interlocked.Increment(ref _planner._certificatesForTests);
                Publish(NavPath.Unreachable);
                return;
            }

            (int layer, int x, int z) = _planner.Decode(component[_componentIndex++]);
            BeginProgress(layer, x, z, ProgressPurpose.CertificateNode);
        }
    }
}
