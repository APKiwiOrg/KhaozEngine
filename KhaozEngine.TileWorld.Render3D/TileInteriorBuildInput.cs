using System.Collections.Generic;
using KhaozEngine.Primitives;

namespace KhaozEngine.TileWorld;

internal sealed record TileInteriorBuildInput(TileWorldDocument Document, TileWorldCatalogs Catalogs,
    ITileMeshGeometry Meshes, InteriorWindow Window, TileInteriorRoofSamples Samples);

internal sealed record TileInteriorBuildOutput(InteriorHeightField Field, IReadOnlyList<string> Issues);
