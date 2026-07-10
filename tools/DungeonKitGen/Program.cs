using System;
using System.Collections.Generic;
using System.Globalization;
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
// Run once per kit scale, the output .glb files are committed.
//
// Args: [outputDir] [cellSizeMeters] [floorHeightMeters]. Every piece dimension is derived from the two
// scale parameters, so a caller with a differently-scaled DungeonConfig (see KhaozEngine.Dungeon) can bake
// a matching kit. Defaults (2 m cell / 4 m floor) are the general-purpose scale (matches DungeonConfig's
// own defaults). KhaozEngine.Showcase's RoomDungeon demo runs its own DungeonConfig at cell=3/floorHeight=6
// (a grander, more cavernous feel), so its committed kit was baked with
// `dotnet run --project tools/DungeonKitGen -- KhaozEngine.Showcase/assets/dungeon 3 6` - re-run the same
// command to regenerate it after a change here (see assets/dungeon/CREDITS.md).
const float DefaultCell = 2.0f;
const float DefaultFloorHeight = 4.0f;
float Cell = args.Length > 1 ? float.Parse(args[1], CultureInfo.InvariantCulture) : DefaultCell;
float FloorHeight = args.Length > 2 ? float.Parse(args[2], CultureInfo.InvariantCulture) : DefaultFloorHeight;
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

// Slab thickness is a fixed nominal 0.2 m regardless of cell size, matching DungeonStamp's own
// ThinHalfThickness (0.1 m half-thickness) for the floor-slab and stair-ramp physics statics - the visual
// slab should stay exactly as thin as the collision it represents, not scale with the footprint.
const float SlabThickness = 0.2f;

// dungeon_floor: Cell x SlabThickness x Cell slab, base at y=0.
Build(Path.Combine(dir, "dungeon_floor.glb"), gray, new[]
{
    (new Vector3(0f, SlabThickness / 2f, 0f), new Vector3(Cell, SlabThickness, Cell)),
});

// dungeon_wall: Cell x FloorHeight x Cell full-cell column, base at y=0.
Build(Path.Combine(dir, "dungeon_wall.glb"), gray, new[]
{
    (new Vector3(0f, FloorHeight / 2f, 0f), new Vector3(Cell, FloorHeight, Cell)),
});

// dungeon_doorframe: two jambs at the cell's +-X edges (outer face flush with the cell footprint) plus a
// lintel spanning the top, one mesh. Every dimension scales proportionally off the original 2 m/4 m
// authoring (jamb footprint 0.15 x cell, jamb height 0.75 x floorHeight, lintel height 0.1 x floorHeight),
// so the doorframe keeps the same silhouette proportions - including its ~0.85 x floorHeight total height
// relative to the wall - at any kit scale.
{
    float jambW = 0.15f * Cell, jambH = 0.75f * FloorHeight, jambD = 0.15f * Cell;
    float lintelH = 0.1f * FloorHeight;
    float jambX = Cell / 2f - jambW / 2f;
    Build(Path.Combine(dir, "dungeon_doorframe.glb"), gray, new[]
    {
        (new Vector3(-jambX, jambH / 2f, 0f), new Vector3(jambW, jambH, jambD)),
        (new Vector3(jambX, jambH / 2f, 0f), new Vector3(jambW, jambH, jambD)),
        (new Vector3(0f, jambH + lintelH / 2f, 0f), new Vector3(Cell, lintelH, jambD)),
    });
}

// dungeon_stair: 8 steps climbing along +Z, rising FloorHeight over a run of two cells, Cell wide. Each
// step is modeled as a solid block from the base up to its tread height (the standard greybox stair
// solid), so the mesh silhouette is a stepped ramp, total height FloorHeight at the top (far, +Z) step,
// matching DungeonStamp.BuildStairRamps's pitched physics ramp, which rises the same FloorHeight over the
// same 2*Cell run.
{
    const int steps = 8;
    float totalRise = FloorHeight, totalRun = 2f * Cell;
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
    (new Vector3(0f, SlabThickness / 2f, 0f), new Vector3(Cell, SlabThickness, Cell)),
});
