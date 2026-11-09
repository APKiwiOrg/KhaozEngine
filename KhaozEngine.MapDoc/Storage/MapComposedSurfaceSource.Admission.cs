using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
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
            if (DemandDiagnostics.Check(this, "MapComposedSurfaceSource.Admission.MaxPageReads.1", "MaxPageReads", _pageVisits, 1, _scope.Limits.MaxPageReads, _pageVisits >= _scope.Limits.MaxPageReads, CapacityDemandLifetime.Cumulative, allowanceSource: _scope.Limits)) throw new MapSurfaceCapacityException();
            PageVisiting?.Invoke();
            _pageVisits++;
        }

        void AdmitMetadata()
        {
            if (DemandDiagnostics.IsObserved)
                foreach (var (origin, increment, over, saturated) in MapPageBudget.PageDemand(_scope.Limits, _scope.Limits.MaxPageReads, _metadata))
                    DemandDiagnostics.Check(this, "MapComposedSurfaceSource.Admission.MaxPageMetadataChecks.2", origin.LimitName, origin.Consumed, increment, origin.Allowance, over, CapacityDemandLifetime.Cumulative, origin.Identity, saturated);
            if (DemandDiagnostics.Check(this, "MapComposedSurfaceSource.Admission.MaxPageMetadataChecks.1", "MaxPageMetadataChecks", _metadata, 1, _workLimit, _metadata >= _workLimit, CapacityDemandLifetime.Cumulative)) throw new MapSurfaceCapacityException();
            MetadataInspecting?.Invoke();
            _metadata++;
        }

        void AdmitRange()
        {
            if (DemandDiagnostics.IsObserved)
                foreach (var (origin, increment, over, saturated) in MapPageBudget.PageDemand(_scope.Limits, _scope.Limits.MaxPageReads, _ranges))
                    DemandDiagnostics.Check(this, "MapComposedSurfaceSource.Admission.MaxPageRangeOperations.2", origin.LimitName, origin.Consumed, increment, origin.Allowance, over, CapacityDemandLifetime.Cumulative, origin.Identity, saturated);
            if (DemandDiagnostics.Check(this, "MapComposedSurfaceSource.Admission.MaxPageRangeOperations.1", "MaxPageRangeOperations", _ranges, 1, _workLimit, _ranges >= _workLimit, CapacityDemandLifetime.Cumulative)) throw new MapSurfaceCapacityException();
            RangeAdding?.Invoke();
            _ranges++;
        }
    }
}
