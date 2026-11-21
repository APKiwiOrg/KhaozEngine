using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    /// <summary>The next operation of the shared search kernel, or Done after publication.</summary>
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

    /// <summary>One cursor through start resolution, membership, the certificate, A* and publication.
    /// The synchronous entry keeps it on the stack and runs the same operations without a quota.</summary>
    partial struct SearchKernel<TGoal> where TGoal : struct, ISearchGoal
    {
        readonly GridPathPlanner _planner;
        readonly SearchScratch _scratch;
        readonly TGoal _goal;
        readonly float _radius;
        readonly PathQueryBudget _budget;
        readonly bool _useCertificate;
        readonly Vector2? _pointGoal;

        int _startId, _startLayer, _startX, _startZ;
        int _current, _layer, _x, _z, _width, _layerBase;
        int _closest, _expanded, _sequence, _neighbor, _link, _wrapOffset;
        double _closestProgress, _gCurrent;
        NavGrid _grid;
        List<PlannerLink>? _links;
        bool _reachedGoal;

        readonly NavGoalRegion? _region;
        readonly Vector3 _feet;
        NavSpace.LayerCursor _layerProbe;
        SnapCursor _snap;
        int _boxLayer, _flagCount, _memberLayer, _memberX, _memberZ;

        int _component, _componentIndex, _progressIndex;
        ProgressPurpose _progressPurpose;
        Vector3 _progressFeet;
        double _startProgress, _progress;

        int _parent, _waypoint;
        NavWaypoint[]? _resultArray;

        SearchKernel(GridPathPlanner planner, SearchScratch scratch, TGoal goal, float radius,
            PathQueryBudget budget, bool useCertificate, Vector2? pointGoal = null)
        {
            _planner = planner;
            _scratch = scratch;
            _goal = goal;
            _radius = radius;
            _budget = budget;
            _useCertificate = useCertificate;
            _pointGoal = pointGoal;
            _grid = planner._space.Layers[0];
        }

        internal SearchKernel(GridPathPlanner planner, SearchScratch scratch, int startLayer, Vector2 startPoint,
            TGoal goal, Vector2 pointGoal, float radius, PathQueryBudget budget, bool useCertificate)
            : this(planner, scratch, goal, radius, budget, useCertificate, pointGoal)
        {
            SetStart(startLayer, startPoint);
            BeginCertificate();
        }

        internal SearchPhase Phase { get; private set; }
        internal NavPath? Result { get; private set; }

        internal NavPath RunUnbounded()
        {
            while (Phase != SearchPhase.Done) Step();
            return Result!;
        }

        internal void RunSlice(int quota, ref int spent)
        {
            while (Phase != SearchPhase.Done)
            {
                int weight = PathWorkWeights.For(Phase);
                if (spent != 0 && weight > quota - spent) return;
                // Comparisons within one reduction share a weight. Run as many as fit even when
                // the node's reduction spans several calls.
                int operations = Phase is SearchPhase.CertificateProgress or SearchPhase.SearchProgress
                    ? Math.Min(_goal.MemberCount - _progressIndex, Math.Max(1, (quota - spent) / weight))
                    : 1;
                Step(operations);
                spent += operations * weight;
            }
        }

        void Step(int operations = int.MaxValue)
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
                case SearchPhase.Seed: SeedOpen(); break;
                case SearchPhase.Pop: Pop(); break;
                case SearchPhase.Neighbor: TestNeighbors(operations); break;
                case SearchPhase.Link: TestLinks(operations); break;
                case SearchPhase.Parent: VisitParent(); break;
                case SearchPhase.Reverse: ReverseChain(operations); break;
                case SearchPhase.Decode: DecodeWaypoint(); break;
                case SearchPhase.Allocate: AllocateResult(); break;
                case SearchPhase.Copy: CopyWaypoints(operations); break;
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

        void SeedOpen()
        {
            _scratch.BeginSearch();
            _scratch.Relax(_startId, 0d, -1);
            _scratch.Open.Enqueue(_startId, (_goal.Priority(_startLayer, _startX, _startZ), _sequence++));
            _closest = _startId;
            BeginProgress(_startLayer, _startX, _startZ, ProgressPurpose.SearchStart);
        }

        void NextPop()
        {
            if (_scratch.Open.Count != 0 && _expanded < _budget.MaxExpandedNodes)
                Phase = SearchPhase.Pop;
            else
                BeginReconstruction();
        }

        void Pop()
        {
            _current = _scratch.Open.Dequeue();
            if (_scratch.IsClosed(_current))
            {
                NextPop(); // A stale duplicate left behind by an earlier relaxation.
                return;
            }

            _scratch.Close(_current);
            _expanded++;
            (_layer, _x, _z) = _planner.Decode(_current);
            _grid = _planner._space.Layers[_layer];
            BeginProgress(_layer, _x, _z, ProgressPurpose.PoppedNode);
        }

        void FinishPoppedNode(double progress)
        {
            if (progress < _closestProgress)
            {
                _closestProgress = progress;
                _closest = _current;
            }

            if (_goal.IsGoal(_layer, _x, _z))
            {
                // Pin a popped success even when it ties the start's partial-progress score.
                _reachedGoal = true;
                _closest = _current;
                BeginReconstruction();
                return;
            }

            _gCurrent = _scratch.GScore(_current);
            _width = _grid.Width;
            _layerBase = _planner._layerOffset[_layer];
            _neighbor = 0;
            Phase = SearchPhase.Neighbor;
        }

        void TestNeighbors(int operations)
        {
            int end = _neighbor + Math.Min(operations, NeighborDx.Length - _neighbor);
            for (int index = _neighbor; index < end; index++)
                RelaxNeighbor(index);
            _neighbor = end;
            if (end < NeighborDx.Length) return;

            if (_planner._linkEdges.TryGetValue(_current, out _links))
            {
                _link = 0;
                Phase = SearchPhase.Link;
            }
            else
                NextPop();
        }

        void RelaxNeighbor(int index)
        {
            int nx = _x + NeighborDx[index], nz = _z + NeighborDz[index];
            if (!_grid.InBounds(nx, nz) || _planner.Blocks(_layer, _radius, nx, nz) ||
                !_planner.CanWalkNeighbor(_layer, _x, _z, nx, nz)) return;

            bool diagonal = index >= 4;
            if (diagonal && (_planner.Blocks(_layer, _radius, nx, _z) ||
                _planner.Blocks(_layer, _radius, _x, nz))) return;

            int id = _layerBase + nz * _width + nx;
            if (_scratch.IsClosed(id)) return;
            double tentative = _gCurrent + PlannerWalkingPrices.Cost(_grid.CellSize, diagonal);
            Relax(id, tentative, _layer, nx, nz);
        }

        void TestLinks(int operations)
        {
            List<PlannerLink> links = _links!;
            int end = _link + Math.Min(operations, links.Count - _link);
            for (int index = _link; index < end; index++)
                RelaxLink(links[index]);
            _link = end;
            if (end == links.Count) NextPop();
        }

        void RelaxLink(PlannerLink link)
        {
            (int target, float cost, NavLinkKind kind) = link;
            if (_budget.WalkOnly && kind is NavLinkKind.Hop or NavLinkKind.Drop) return;
            if (_scratch.IsClosed(target)) return;

            (int layer, int x, int z) = _planner.Decode(target);
            if (_planner.Blocks(layer, _radius, x, z) ||
                !_planner.CanTraverse(_layer, _x, _z, layer, x, z)) return;
            Relax(target, _gCurrent + cost, layer, x, z, kind);
        }

        void Relax(int id, double tentative, int layer, int x, int z, NavLinkKind? kind = null)
        {
            if (tentative < _scratch.GScore(id))
            {
                _scratch.Relax(id, tentative, _current, kind);
                double f = tentative + _goal.Priority(layer, x, z);
                _scratch.Open.Enqueue(id, (f, _sequence++));
            }
        }
    }
}
