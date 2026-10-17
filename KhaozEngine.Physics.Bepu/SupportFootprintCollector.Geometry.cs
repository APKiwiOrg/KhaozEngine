using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

internal sealed partial class SupportFootprintCollector
{
    // The two geometry families have large, mutually exclusive value temporaries.
    // Keep their frames separate from capture and from each other.
    [MethodImpl(MethodImplOptions.NoInlining)]
    CapsuleFeatureStatus CollectPolygons()
    {
        foreach (SupportPolygon polygon in _polygons)
        {
            GeometrySign admitted;
            SupportFootprintMember value;
            using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportExtrema))
                admitted = SupportFootprintPolygon.Measure(polygon, _request, _bounds,
                    _columnBottom, _columnTop, out value);
            if (admitted == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unsupported;
            if (admitted == GeometrySign.Negative) continue;
            byte mask = 0;
            using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportContacts))
            {
                for (int proposal = 0; proposal < 2; proposal++)
                {
                    if (_verdicts[proposal].Status != SupportProposalStatus.Contact) continue;
                    GeometrySign member = ContactMember(polygon, proposal);
                    if (member == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unsupported;
                    if (member != GeometrySign.Positive) continue;
                    mask |= (byte)(1 << proposal);
                    if (!_belowBandPass && !ContactHeight(polygon, proposal, sourceHeight: true)) return CapsuleFeatureStatus.Unsupported;
                }
            }
            CapsuleFeatureStatus retained = Retain(new(polygon, default, 0, value with { ProposalMask = mask }));
            if (retained != CapsuleFeatureStatus.Complete) return retained;
        }
        return CapsuleFeatureStatus.Complete;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    CapsuleFeatureStatus CollectCurves(StaticHandle owner)
    {
        using var phase = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportCurved);
        foreach (ref readonly SupportCurvedLeaf leaf in CollectionsMarshal.AsSpan(_curved))
            for (int part = 0; part < leaf.Parts; part++)
            {
                GeometrySign admitted = _curvedKernel.Measure(owner, leaf, part, _request, out SupportFootprintMember value);
                if (admitted == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unsupported;
                if (admitted == GeometrySign.Negative) continue;
                byte mask = 0;
                for (int proposal = 0; proposal < 2; proposal++)
                {
                    if (_verdicts[proposal].Status != SupportProposalStatus.Contact) continue;
                    GeometrySign context = CurvedContactMember(owner, leaf, part, proposal);
                    if (context == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unsupported;
                    if (context != GeometrySign.Positive) continue;
                    mask |= (byte)(1 << proposal);
                    if (!_belowBandPass && !CurvedContactHeight(leaf, part, proposal)) return CapsuleFeatureStatus.Unsupported;
                }
                CapsuleFeatureStatus retained = Retain(new(null, leaf, part, value with { ProposalMask = mask }));
                if (retained != CapsuleFeatureStatus.Complete) return retained;
            }
        return CapsuleFeatureStatus.Complete;
    }
}
