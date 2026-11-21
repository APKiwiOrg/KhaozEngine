using System;
using System.Numerics;
using System.Threading;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    partial struct SearchKernel<TGoal>
    {
        enum ProgressPurpose { CertificateStart, CertificateNode, SearchStart, PoppedNode }

        void BeginCertificate()
        {
            // Rounded legacy snaps can decode to another cell. Only the actual search node can anchor a proof.
            if (!_useCertificate || !_planner._space.Layers[_startLayer].InBounds(_startX, _startZ))
            {
                BeginSearch();
                return;
            }

            _component = _planner._connectivity.ComponentOf(_startId);
            if (_component < 0)
            {
                BeginSearch();
                return;
            }

            int nodes = _planner._connectivity.Members(_component).Length;
            int members = _goal.MemberCount;
            if (nodes > CertificateNodeLimit || members <= 0 || (long)nodes * members > CertificateComparisonLimit)
                BeginSearch();
            else
                Phase = SearchPhase.CertificateLabel;
        }

        void CheckCertificateLabel()
        {
            int label = _planner._connectivity.ComponentOf(_goal.MemberNode(_progressIndex++));
            if (label < 0 || label == _component)
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
            for (int index = _progressIndex; index < end; index++)
            {
                Vector3 member = _goal.MemberFeet(index);
                double dx = (double)feet.X - member.X;
                double dy = (double)feet.Y - member.Y;
                double dz = (double)feet.Z - member.Z;
                progress = Math.Min(progress, dx * dx + dy * dy + dz * dz);
            }
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
            if (!double.IsFinite(_progress) || _progress < _startProgress)
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
