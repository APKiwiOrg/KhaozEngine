using System;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc.Storage;

internal sealed partial class MapComposedSurfaceSource
{
    // One composed ceiling for page visits, metadata inspections and rectangle additions across every native and
    // generated part. Parts report work before doing it, so a public adapter that copies the request cannot
    // multiply the allowance by discarding the internal shared budget.
    sealed partial class Acquisition
    {
        readonly long _workLimit;
        long _metadata, _ranges;
        string? _unobservable;
        public bool CanObserveWork { get; private set; } = true;
        public event Action? MetadataInspecting;
        public event Action? RangeAdding;

        static string? Unobservable(IMapSurfaceAcquisitionSession session, bool pages, bool work)
        {
            var missing = new List<string>();
            if (pages && session is not IMapSurfacePageVisitSession { CanObservePageVisits: true })
                missing.Add(nameof(IMapSurfacePageVisitSession) + "." + nameof(IMapSurfacePageVisitSession.CanObservePageVisits));
            if (work && session is not IMapSurfaceWorkSession { CanObserveWork: true })
                missing.Add(nameof(IMapSurfaceWorkSession) + "." + nameof(IMapSurfaceWorkSession.CanObserveWork));
            return missing.Count == 0 ? null : "composed surface acquisition refused: a native session lacks " +
                string.Join(" and ", missing) + ", so its work cannot share the composed ceiling. Raising query limits cannot cure this.";
        }

        void Observe(IMapSurfaceAcquisitionSession session, ref int pageOpaque, ref int workOpaque)
        {
            if (session is IMapSurfacePageVisitSession { CanObservePageVisits: true } pages) pages.PageVisiting += AdmitPageVisit;
            else pageOpaque++;
            if (session is IMapSurfaceWorkSession { CanObserveWork: true } work)
            {
                work.MetadataInspecting += AdmitMetadata;
                work.RangeAdding += AdmitRange;
            }
            else workOpaque++;
        }

        // Both capabilities are fixed for a session lifetime, so release matches the original subscription.
        void Release(IMapSurfaceAcquisitionSession session)
        {
            if (session is IMapSurfacePageVisitSession { CanObservePageVisits: true } pages) pages.PageVisiting -= AdmitPageVisit;
            if (session is IMapSurfaceWorkSession { CanObserveWork: true } work)
            {
                work.MetadataInspecting -= AdmitMetadata;
                work.RangeAdding -= AdmitRange;
            }
        }

        void AdmitPageVisit()
        {
            if (_pageVisits >= _scope.Limits.MaxPageReads) throw new MapSurfaceCapacityException();
            PageVisiting?.Invoke();
            _pageVisits++;
        }

        void AdmitMetadata()
        {
            if (_metadata >= _workLimit) throw new MapSurfaceCapacityException();
            MetadataInspecting?.Invoke();
            _metadata++;
        }

        void AdmitRange()
        {
            if (_ranges >= _workLimit) throw new MapSurfaceCapacityException();
            RangeAdding?.Invoke();
            _ranges++;
        }
    }
}
