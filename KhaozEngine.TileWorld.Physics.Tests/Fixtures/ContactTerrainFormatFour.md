# Terrain format 4 golden

`ContactTerrainFormatFour.navbake` was baked on macOS ARM64 with engine identity 23.1.0.
It contains the synthetic four-metre analytic rim used by `ContactTerrainScene`, the standard test
capture options and one enabled player profile with a six-metre allowance. The world source digest
is 32 zero bytes. It contains eight accepted terrain links. The identity binds the canonical player publication policy through
the engine-owned `engine/player-publication` source digest. It was rebaked after that policy changed the
certified step, retaining the same geometry and accepted terrain links.

`ContactTerrainGoldenTests` loads these committed bytes without physics, reconstructs every accepted
terrain certificate on first use and requires an identical rewrite. It supplies the stored engine
identity explicitly, so a later engine version does not invalidate this format regression fixture.
The Terrain format portability workflow runs that same artifact on hosted Linux, macOS and Windows.
