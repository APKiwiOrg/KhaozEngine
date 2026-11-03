# Renderer assertion helpers

These helpers belong to the test assembly and can be checked headlessly.

`GoldenCompare.CompareOrUpdate` returns null on a successful compare or bake. A failed check returns
`GoldenComparisonFailure`, with `Reason` and the evidence paths it wrote (`GotPath`, `WantPath`, `DiffPath`)
or the caller's `BakeRefusal` payload. `ImageRegressed` reports a comparison over tolerance, `MissingGolden`
reports an absent backend reference, and `BakeRefused` stops a bake before writing anything.
`GoldenCompare.AssertOrUpdate` uses the same result and throws the existing xUnit `FailException` through
`GoldenComparisonFailure.Throw`. Messages and evidence filenames keep their existing format.

`MotionExpectation` throws `MotionExpectationException`, an xUnit assertion failure. Its `Reason` distinguishes
`NoDrawnPixels`, `OutsideTolerance`, `InvalidCoveredMotion` and `NoAdmittedPixels`. `Pixel` names the offending
location, and is absent for an empty scan. `Count` records how many pixels were checked. Tolerance failures
also carry the complete `Scan`, and failed coverage checks carry the raw `Stored` motion.
