using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    partial struct RequestStepper
    {
        internal RequestStepper(GridPathPlanner planner, SearchScratch scratch, Vector3 feet, NavGoalRegion region,
            float radius, PathQueryBudget budget)
        {
            _planner = planner;
            _scratch = scratch;
            _goal = planner.CreateRegionGoal(scratch, region);
            _radius = radius;
            _budget = budget;
            _grid = planner._space.Layers[0];
            _region = region;
            _feet = feet;
            _layerProbe = new NavSpace.LayerCursor(planner._space, feet);
            if (_layerProbe.Done) BeginSnap();
            else Phase = SearchPhase.Layer;
        }

        void ProbeLayer()
        {
            _layerProbe.Probe();
            if (_layerProbe.Done) BeginSnap();
        }

        void BeginSnap()
        {
            int layer = _layerProbe.Layer;
            _snap = new SnapCursor(_planner, layer, _planner._space.Layers[layer],
                new Vector2(_feet.X, _feet.Z), _radius, _budget.SnapRadius);
            if (_snap.Done) Publish(NavPath.Unreachable);
            else Phase = SearchPhase.Snap;
        }

        void ProbeSnap()
        {
            _snap.Probe();
            if (!_snap.Done) return;
            if (_snap.Result is not Vector2 position)
            {
                Publish(NavPath.Unreachable);
                return;
            }

            SetStart(_layerProbe.Layer, position);
            _scratch.ResetMembers();
            Phase = SearchPhase.MemberBox;
        }

        void PrepareMemberBox()
        {
            (int x0, int z0, int x1, int z1) = RegionCellBounds(_planner._space.Layers[_boxLayer], _region);
            int width = x1 - x0 + 1, height = z1 - z0 + 1;
            _flagCount = _scratch.SetMemberBox(_boxLayer, x0, z0, width, height, _flagCount);
            if (width > 0 && height > 0)
                _scratch.RecordCandidateLayer(_boxLayer, _candidateLayerCount++);

            if (++_boxLayer < _planner._space.Layers.Count) return;
            if (_candidateLayerCount == 0)
            {
                Publish(NavPath.Unreachable);
                return;
            }

            _scratch.EnsureMemberFlags(_flagCount);
            BeginMemberLayer();
            Phase = SearchPhase.Member;
        }

        void BeginMemberLayer()
        {
            _memberLayer = _scratch.CandidateLayer(_memberLayerIndex);
            SearchScratch.MemberBox box = _scratch.CandidateBox(_memberLayer);
            _memberX = box.X0;
            _memberZ = box.Z0;
        }

        void TestMember()
        {
            int layer = _memberLayer;
            _planner.TestRegionMember(_scratch, _region, _radius, _planner._space.Layers[layer], layer,
                _planner._layerOffset[layer], _memberX, _memberZ);

            SearchScratch.MemberBox box = _scratch.CandidateBox(layer);
            if (++_memberX - box.X0 < box.Width) return;
            _memberX = box.X0;
            if (++_memberZ - box.Z0 < box.Height) return;
            if (++_memberLayerIndex < _candidateLayerCount)
                BeginMemberLayer();
            else if (_scratch.Members.Count > 0)
                BeginCertificate();
            else
                Publish(NavPath.Unreachable);
        }
    }
}
