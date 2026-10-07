# Legacy finite-feature eligibility

This private predicate qualifies an already complete feature result. It does not query geometry,
change a pose, or replace rise, selected-view clearance, footprint or navigation-layer checks.
The movement call site remains unchanged until the runtime repair gate opens.

Complete results are authenticated through their original capability and lease before consuming
geometry. Non-Complete results refuse. The numerical ceilings remain 0.25 mm position and 0.00001
normal error. The two error-denominator comparisons multiply a binary32 value by 4000 or 100000.
Those products need at most 41 significand bits and cannot overflow binary64, so the comparisons are
exact. No provider uncertainty is ignored because it is small.

The entire separation interval must fit the same represented `0.0001f` band requested by the caller.
That band is slightly inside exact 0.1 mm. A downward-rounded component bound is obtained by one
binary64 subtraction followed by BitDecrement. Exact zero-error components bypass that subtraction,
retaining a proved vertical face's zero upward derivative. Separation width is rounded upward before
comparison. Every interval must be wholly on the accepted side of the existing slope/flatness limits.

For a genuine closest pair between an upright axis segment and finite geometry, a strictly upward
separation direction proves that the axis witness is the lower endpoint. Otherwise a small downward
axis displacement toward the same geometry witness would reduce squared distance. This is a
consequence of certified closestness, not of comparing a rounded axis point with a tolerance.

FaceInterior and OpenBoundary may retain multiple raw coplanar triangles belonging to the one proved
patch. Every incident face must qualify as supporting that patch. A ConvexCrease has two actual
incident faces and requires exactly one near-flat supporting face. Every incident face must prove a
nonnegative upward derivative. The complete convex finite closest-feature relation supplies the
normal-cone optimality condition. A normal-error ball or nearby sweep point cannot establish it alone.
Concave and vertex neighborhoods refuse in this initial consumer. Disconnected alternatives already
refuse the geometry query. Underside, wall and uncertain derivative/threshold cases cannot qualify.

The qualification is local to the supplied candidate. It neither snaps to an infinite plane nor
certifies a finite upward path. Existing movement protections and the unchanged 1 mm navigation
arrival condition remain mandatory. The twelve real lip-corner rows and independent topology/capacity
controls are retained in `docs/verification/2026-10-08-low-lip-feature-correspondence.json`.

Focused verification passed all fifteen real-neighborhood, wider-enclosure, lifetime and patch
cases. Independent source/math review and scoped format passed. Evidence is recorded in
`docs/verification/2026-10-08-low-lip-eligibility-green.json`. The movement call site is byte-unchanged.
