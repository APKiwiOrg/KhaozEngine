# Terrain format 4 golden

`ContactTerrainFormatFour.navbake` was baked on macOS ARM64 with engine identity 22.0.1.
It contains the synthetic four-metre analytic rim used by `ContactTerrainScene`, the standard test
capture options and one enabled player profile with a six-metre allowance. The world source digest
is 32 zero bytes. It contains eight accepted terrain links.

`ContactTerrainGoldenTests` loads these committed bytes without physics, reconstructs every accepted
terrain certificate on first use and requires an identical rewrite. It supplies the stored engine
identity explicitly, so a later engine version does not invalidate this format regression fixture.
The Terrain format portability workflow runs that same artifact on hosted Linux, macOS and Windows.
