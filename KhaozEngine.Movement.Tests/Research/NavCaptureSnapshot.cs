using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Movement;

namespace KhaozEngine.Tests.Movement.Research;

/// <summary>Test-only restoration of diagnostic columns against caller-rebuilt static physics.</summary>
public static class NavCaptureSnapshot
{
    public static PhysicsNavBake Restore(NavCaptureResearchInput input, string path)
    {
        ArgumentNullException.ThrowIfNull(input);
        var world = input.Context.Physics ?? throw new ArgumentException("Restoration requires rebuilt physics.");
        PhysicsNavCaptureBasis? basis = PhysicsNavCaptureBasis.Capture(world);
        Vector3 origin = basis?.Origin ?? world.Origin;
        var layout = input.Options.Validate(origin);
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadString() != "PCAP1" || reader.ReadInt32() != layout.Width || reader.ReadInt32() != layout.Height)
            throw new InvalidDataException("Snapshot layout differs from the supplied options.");
        Vector3 recordedOrigin = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        if (!SameBits(origin.X, recordedOrigin.X) || !SameBits(origin.Y, recordedOrigin.Y) ||
            !SameBits(origin.Z, recordedOrigin.Z))
            throw new InvalidDataException("Snapshot origin differs from rebuilt physics.");
        var starts = new int[layout.Cells + 1];
        var surfaces = new List<PhysicsNavSurface>();
        for (int cell = 0; cell < layout.Cells; cell++)
        {
            starts[cell] = surfaces.Count;
            int count = reader.ReadInt32();
            if (count < 0 || count > input.Options.MaxSurfacesPerColumn)
                throw new InvalidDataException("Snapshot surface count exceeds the supplied cap.");
            float previous = float.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                float height = reader.ReadSingle(), headroom = reader.ReadSingle();
                uint areas = reader.ReadUInt32();
                if (!float.IsFinite(height) || height <= previous || float.IsNaN(headroom) || headroom < 0f)
                    throw new InvalidDataException("Snapshot surfaces are not valid ascending columns.");
                surfaces.Add(new PhysicsNavSurface(height, headroom, areas));
                previous = height;
            }
        }
        starts[layout.Cells] = surfaces.Count;
        int wet = reader.ReadInt32();
        if (wet < 0 || wet > layout.Cells || (!input.Options.SampleWater && wet != 0))
            throw new InvalidDataException("Snapshot water count differs from the supplied options.");
        var water = new PhysicsNavWater[wet];
        int lastCell = -1;
        for (int i = 0; i < wet; i++)
        {
            int cell = reader.ReadInt32();
            float y = reader.ReadSingle();
            uint areas = reader.ReadUInt32();
            if (cell <= lastCell || cell >= layout.Cells || !float.IsFinite(y))
                throw new InvalidDataException("Snapshot water entries are not canonical.");
            water[i] = new PhysicsNavWater(cell, y, areas);
            lastCell = cell;
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException("Snapshot has trailing bytes.");
        basis?.AssertCurrent();
        var columns = PhysicsNavColumns.Own(input.Options, layout.Width, layout.Height, starts, surfaces.ToArray(), water);
        ConstructorInfo constructor = typeof(PhysicsNavBake).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(GroundMoveContext), typeof(PhysicsNavBakeOptions), typeof(Vector3), typeof(PhysicsNavColumns),
                typeof(PhysicsNavCaptureBasis?), typeof(NavAreaClassifier)], null)
            ?? throw new MissingMethodException("PhysicsNavBake diagnostic restoration constructor changed.");
        return (PhysicsNavBake)constructor.Invoke([input.Context, input.Options, origin, columns, basis, input.Classifier]);
    }

    public static void Write(PhysicsNavBake capture, string path) => NavCaptureResearch.WriteColumns(capture, path);

    private static bool SameBits(float a, float b) => BitConverter.SingleToUInt32Bits(a) == BitConverter.SingleToUInt32Bits(b);
}
