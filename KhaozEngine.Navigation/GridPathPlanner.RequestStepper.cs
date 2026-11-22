using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    /// <summary>The next paid request operation, or Done after publication.</summary>
    internal enum SearchPhase
    {
        Layer,
        Snap,
        MemberBox,
        Member,
        CertificateLabel,
        CertificateProgress,
        Wrap,
        Seed,
        Pop,
        SearchProgress,
        Neighbor,
        Link,
        Parent,
        Reverse,
        Decode,
        Allocate,
        Copy,
        Done,
    }

    /// <summary>Resumes a region query at its next operation using a leased, prewarmed workspace.</summary>
    partial struct RequestStepper
    {
        readonly GridPathPlanner _planner;
        readonly SearchScratch _scratch;
        RegionGoal _goal;
        readonly float _radius;
        readonly PathQueryBudget _budget;

        int _startId, _startLayer, _startX, _startZ;
        int _current, _layer, _x, _z, _width, _layerBase;
        int _closest, _expanded, _sequence, _neighbor, _link, _wrapOffset;
        double _closestProgress, _gCurrent;
        NavGrid _grid;
        List<PlannerLink>? _links;
        bool _reachedGoal;

        readonly NavGoalRegion _region;
        readonly Vector3 _feet;
        NavSpace.LayerCursor _layerProbe;
        SnapCursor _snap;
        int _boxLayer, _flagCount, _candidateLayerCount, _memberLayerIndex, _memberLayer, _memberX, _memberZ;

        int _component, _componentIndex, _progressIndex;
        ProgressPurpose _progressPurpose;
        Vector3 _progressFeet;
        double _startProgress, _progress;

        int _parent, _waypoint;
        NavWaypoint[]? _resultArray;

        internal SearchPhase Phase { get; private set; }
        internal NavPath? Result { get; private set; }

        internal void RunSlice(int quota, ref int spent)
        {
            while (Phase != SearchPhase.Done)
            {
                int weight = PathWorkWeights.For(Phase);
                if (spent != 0 && weight > quota - spent) return;
                // Comparisons within one reduction share a weight and keep their exact running minimum.
                int operations = Phase is SearchPhase.CertificateProgress or SearchPhase.SearchProgress
                    ? Math.Min(_goal.MemberCount - _progressIndex, Math.Max(1, (quota - spent) / weight))
                    : 1;
                Step(operations);
                spent += operations * weight;
            }
        }

        void Step(int operations)
        {
            switch (Phase)
            {
                case SearchPhase.Layer: ProbeLayer(); break;
                case SearchPhase.Snap: ProbeSnap(); break;
                case SearchPhase.MemberBox: PrepareMemberBox(); break;
                case SearchPhase.Member: TestMember(); break;
                case SearchPhase.CertificateLabel: CheckCertificateLabel(); break;
                case SearchPhase.CertificateProgress:
                case SearchPhase.SearchProgress: CompareProgress(operations); break;
                case SearchPhase.Wrap: ClearStampBlock(); break;
                case SearchPhase.Seed: SeedSearch(); break;
                case SearchPhase.Pop: Pop(); break;
                case SearchPhase.Neighbor: TestNeighbor(); break;
                case SearchPhase.Link: TestLink(); break;
                case SearchPhase.Parent: VisitParent(); break;
                case SearchPhase.Reverse: ReverseChain(); break;
                case SearchPhase.Decode: DecodeWaypoint(); break;
                case SearchPhase.Allocate: AllocateResult(); break;
                case SearchPhase.Copy: CopyWaypoint(); break;
            }
        }

        void Publish(NavPath path)
        {
            Result = path;
            Phase = SearchPhase.Done;
        }

        void SetStart(int layer, Vector2 position)
        {
            NavGrid grid = _planner._space.Layers[layer];
            _startLayer = layer;
            (_startX, _startZ) = grid.CellOf(position.X, position.Y);
            _startId = _planner._layerOffset[layer] + _startZ * grid.Width + _startX;
        }

        void BeginSearch()
        {
            Interlocked.Increment(ref _planner._searchesForTests);
            Phase = _scratch.NeedsWrap ? SearchPhase.Wrap : SearchPhase.Seed;
        }

        void ClearStampBlock()
        {
            _wrapOffset = _scratch.ClearStampBlock(_wrapOffset);
            if (!_scratch.NeedsWrap) Phase = SearchPhase.Seed;
        }

        void SeedSearch()
        {
            _scratch.BeginSearch();
            SeedOpen(_scratch, _scratch.Open, ref _goal, ref _sequence, _startId, _startLayer, _startX, _startZ);
            _closest = _startId;
            BeginProgress(_startLayer, _startX, _startZ, ProgressPurpose.SearchStart);
        }

        void NextPop()
        {
            if (CanPop(_scratch.Open, _expanded, _budget.MaxExpandedNodes))
                Phase = SearchPhase.Pop;
            else
                BeginReconstruction();
        }

        void Pop()
        {
            _current = _scratch.Open.Dequeue();
            if (!TryClosePoppedNode(_scratch, _current, ref _expanded))
            {
                NextPop();
                return;
            }

            (_layer, _x, _z) = _planner.Decode(_current);
            _grid = _planner._space.Layers[_layer];
            BeginProgress(_layer, _x, _z, ProgressPurpose.PoppedNode);
        }

        void FinishPoppedNode(double progress)
        {
            _reachedGoal = SelectPoppedEndpoint(ref _goal, _current, _layer, _x, _z, progress,
                ref _closest, ref _closestProgress);
            if (_reachedGoal)
            {
                BeginReconstruction();
                return;
            }

            _gCurrent = _scratch.GScore(_current);
            _width = _grid.Width;
            _layerBase = _planner._layerOffset[_layer];
            _neighbor = 0;
            Phase = SearchPhase.Neighbor;
        }

        void TestNeighbor()
        {
            _planner.RelaxNeighbor(_scratch, _scratch.Open, ref _goal, ref _sequence, _current,
                _layer, _x, _z, _grid, _width, _layerBase, _gCurrent, _radius, _neighbor);
            if (++_neighbor < NeighborCount) return;

            _links = _planner.LinksAfterNeighbors(_current, _neighbor);
            if (_links is null)
                NextPop();
            else
            {
                _link = 0;
                Phase = SearchPhase.Link;
            }
        }

        void TestLink()
        {
            List<PlannerLink> links = _links!;
            _planner.RelaxLink(_scratch, _scratch.Open, ref _goal, ref _sequence, _current,
                _layer, _x, _z, _gCurrent, _radius, _budget.WalkOnly, links[_link]);
            if (++_link == links.Count) NextPop();
        }
    }
}
