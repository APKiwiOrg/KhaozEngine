using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

// One-off generator: emits the five greybox KhaozEngine.Dungeon kit pieces (floor, wall, doorframe,
// stair, landing) as plain gray box .glb meshes matching DungeonKitMap.Greybox()'s ids. Each piece is
// authored at its exact final size (PropLoader scales a loaded prop to the manifest heightMeters, so
// authored height == heightMeters means no rescale) with the origin at the piece's base center (y=0 at
// the floor, x/z centered), matching how PropLoader drops the origin to feet and recenters XZ anyway.
// Run once; the output .glb files are committed.

const float Cell = 2.0f; // one dungeon grid cell footprint, matches DungeonPlotTransform's tile size.
var gray = new Vector4(0.5f, 0.5f, 0.55f, 1f);

// Yields the 12 triangles (as CCW-from-outside vertex triples) of an axis-aligned box centered at
// `center` with full extents `size`. A missing NORMAL is computed by the loader from winding order
// (see GltfLoader), so face winding must be outward-facing here.
static IEnumerable<(Vector3 a, Vector3 b, Vector3 c)> BoxTris(Vector3 center, Vector3 size)
{
    Vector3 h = size / 2f;
    Vector3 min = center - h, max = center + h;
    Vector3[] p =
    {
        new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
        new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
    };
    (int a, int b, int c, int d)[] faces =
    {
        (4, 5, 6, 7), // +Z front
        (3, 2, 1, 0), // -Z back
        (1, 2, 6, 5), // +X right
        (0, 4, 7, 3), // -X left
        (3, 7, 6, 2), // +Y top
        (0, 1, 5, 4), // -Y bottom
    };
    foreach ((int a, int b, int c, int d) in faces)
    {
        yield return (p[a], p[b], p[c]);
        yield return (p[a], p[c], p[d]);
    }
}

void Build(string path, Vector4 color, IEnumerable<(Vector3 center, Vector3 size)> boxes)
{
    var mat = new MaterialBuilder("body").WithMetallicRoughnessShader()
        .WithBaseColor(color).WithMetallicRoughness(0f, 1f);
    var mesh = new MeshBuilder<VertexPosition, VertexEmpty, VertexEmpty>("greybox");
    var prim = mesh.UsePrimitive(mat);
    int triCount = 0;
    foreach ((Vector3 center, Vector3 size) in boxes)
        foreach ((Vector3 a, Vector3 b, Vector3 c) in BoxTris(center, size))
        {
            prim.AddTriangle(new VertexPosition(a), new VertexPosition(b), new VertexPosition(c));
            triCount++;
        }
    var scene = new SceneBuilder();
    scene.AddRigidMesh(mesh, Matrix4x4.Identity);
    string full = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    scene.ToGltf2().SaveGLB(full);
    Console.WriteLine($"wrote {full} tris={triCount}");
}

string dir = args.Length > 0 ? args[0] : "KhaozEngine.Showcase/assets/dungeon";

// dungeon_floor: 2.0 x 0.2 x 2.0 slab, base at y=0.
Build(Path.Combine(dir, "dungeon_floor.glb"), gray, new[]
{
    (new Vector3(0f, 0.1f, 0f), new Vector3(Cell, 0.2f, Cell)),
});

// dungeon_wall: 2.0 x 4.0 x 2.0 full-cell column, base at y=0.
Build(Path.Combine(dir, "dungeon_wall.glb"), gray, new[]
{
    (new Vector3(0f, 2.0f, 0f), new Vector3(Cell, 4.0f, Cell)),
});

// dungeon_doorframe: two 0.3 x 3.0 x 0.3 jambs at the cell's +-X edges (outer face flush with the
// cell footprint) plus a 2.0 x 0.4 x 0.3 lintel spanning the top, one mesh, total height 3.4.
{
    const float jambW = 0.3f, jambH = 3.0f, jambD = 0.3f;
    const float lintelH = 0.4f;
    float jambX = Cell / 2f - jambW / 2f;
    Build(Path.Combine(dir, "dungeon_doorframe.glb"), gray, new[]
    {
        (new Vector3(-jambX, jambH / 2f, 0f), new Vector3(jambW, jambH, jambD)),
        (new Vector3(jambX, jambH / 2f, 0f), new Vector3(jambW, jambH, jambD)),
        (new Vector3(0f, jambH + lintelH / 2f, 0f), new Vector3(Cell, lintelH, jambD)),
    });
}

// dungeon_stair: 8 steps climbing along +Z, rising 4.0 over a 4.0 run (two cells), 2.0 wide. Each step
// is modeled as a solid block from the base up to its tread height (the standard greybox stair solid),
// so the mesh silhouette is a stepped ramp; total height 4.0 at the top (far, +Z) step.
{
    const int steps = 8;
    const float totalRise = 4.0f, totalRun = 2f * Cell;
    float riser = totalRise / steps, run = totalRun / steps;
    var stairBoxes = new List<(Vector3 center, Vector3 size)>();
    for (int i = 0; i < steps; i++)
    {
        float stepHeight = (i + 1) * riser;
        float zStart = -totalRun / 2f + i * run;
        float zCenter = zStart + run / 2f;
        stairBoxes.Add((new Vector3(0f, stepHeight / 2f, zCenter), new Vector3(Cell, stepHeight, run)));
    }
    Build(Path.Combine(dir, "dungeon_stair.glb"), gray, stairBoxes);
}

// dungeon_landing: visual twin of dungeon_floor (distinct id so games can style stair arrivals).
Build(Path.Combine(dir, "dungeon_landing.glb"), gray, new[]
{
    (new Vector3(0f, 0.1f, 0f), new Vector3(Cell, 0.2f, Cell)),
});
