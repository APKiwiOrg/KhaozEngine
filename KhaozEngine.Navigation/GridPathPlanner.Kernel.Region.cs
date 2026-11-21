using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    partial struct SearchKernel<TGoal>
    {
        internal SearchKernel(GridPathPlanner planner, SearchScratch scratch, Vector3 feet, NavGoalRegion region,
            TGoal goal, float radius, PathQueryBudget budget, bool useCertificate)
            : this(planner, scratch, goal, radius, budget, useCertificate)
        {
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
            (int x0, int z0, int x1, int z1) = RegionCellBounds(_planner._space.Layers[_boxLayer], _region!);
            _flagCount = _scratch.SetMemberBox(_boxLayer, x0, z0, x1 - x0 + 1, z1 - z0 + 1, _flagCount);
            if (++_boxLayer < _planner._space.Layers.Count) return;
            if (_scratch.MemberLayerCount == 0)
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
            SearchScratch.MemberBox box = _scratch.CandidateBox(_scratch.MemberLayers[_memberLayer]);
            _memberX = box.X0;
            _memberZ = box.Z0;
        }

        void TestMember()
        {
            int layer = _scratch.MemberLayers[_memberLayer];
            bool member = _planner.IsRegionMember(_region!, _radius, layer, _memberX, _memberZ, out Vector3 feet);
            _scratch.SetMember(layer, _memberX, _memberZ, member);
            if (member)
            {
                int id = _planner._layerOffset[layer] + _memberZ * _planner._space.Layers[layer].Width + _memberX;
                _scratch.AddMember(feet, _memberX, _memberZ, id);
            }

            SearchScratch.MemberBox box = _scratch.CandidateBox(layer);
            if (++_memberX - box.X0 < box.Width) return;
            _memberX = box.X0;
            if (++_memberZ - box.Z0 < box.Height) return;
            if (++_memberLayer < _scratch.MemberLayerCount)
                BeginMemberLayer();
            else if (_scratch.Members.Count > 0)
                BeginCertificate();
            else
                Publish(NavPath.Unreachable);
        }
    }
}
