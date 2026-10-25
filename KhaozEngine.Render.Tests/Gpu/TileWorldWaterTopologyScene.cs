using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using KhaozEngine.Tests.TileWorld;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Lazy shared-device topology acceptance through the shipped TileWorld adapter. Every case renders the authored
/// bed, the scene-depth control and the opaque control from the same document and camera, then an independent
/// field built from the drawn triangles and a negative witness built from a wrong topology. The saddle's look,
/// lighting and 0.20 m feather are used unchanged. All cases run once, in a fixed order, which also ages the
/// scene for the fresh-scene parity fact.
/// </summary>
public sealed class TileWorldWaterTopologyScene : IDisposable
{
    public const int Size = 512;
    const int Frames = 2;
    const float DeepMetres = 0.30f;
    const float VisibleInfluence = 0.08f;

    GpuDeviceContext? _gpu;
    IGpuTexture? _target;
    IGpuFramebuffer? _framebuffer;
    IGpuCommandList? _commands;
    Scene3D? _scene;
    FlyCamera3D? _camera;
    Dictionary<string, TileWaterTopologyCapture>? _results;
    bool _disposed;

    public string Backend => Device().Backend.ToString();
    public string DeviceName => Device().Capabilities.DeviceName;
    public string DirectoryPath => Path.Combine(
        Environment.GetEnvironmentVariable("KE_WATER_OPTICS_OUTPUT") is { Length: > 0 } root
            ? root : "/tmp/khaozengine-water-triangles/task-5", "topology", Backend);

    internal TileWaterTopologyCapture Result(TileWaterTopologyCase c)
    {
        if (_results is null)
        {
            var results = new Dictionary<string, TileWaterTopologyCapture>();
            foreach (TileWaterTopologyCase each in TileWorldWaterTopologyFixtures.All) results.Add(each.Name, Capture(each));
            _results = results;
        }
        return _results[c.Name];
    }

    internal TileWaterTopologyCapture Capture(TileWaterTopologyCase c, string? label = null)
    {
        var total = Stopwatch.StartNew();
        IGpuDevice device = Device();
        Scene3D scene = _scene!;
        var result = new TileWaterTopologyCapture { Name = label ?? c.Name, ExpectedFields = c.ExpectedFields };
        TileWorldDocument doc = c.Build();
        Configure(c);
        var look = new WaterLook
        {
            WaveSource = WaterWaveSource.Procedural, SwellAmplitude = 0f,
            NormalStrength = 0f, ShoreFadeDistance = 0.20f, FoamStrength = 0f, GlintStrength = 0f,
        };
        var recorder = new TileWorldWaterTopologyRecorder(new Scene3DTileWorldScene(scene));
        var authoredOptions = new TileWorldViewOptions
        {
            WaterDepthMode = TileWaterDepthMode.AuthoredBed, WaterLook = look,
            Mesher = new TileGroundMesherOptions { JitterAmplitude = c.JitterAmplitude },
        };
        var legacyOptions = new TileWorldViewOptions
        {
            WaterLook = look, Mesher = new TileGroundMesherOptions { JitterAmplitude = c.JitterAmplitude },
        };
        TileWorldView? authored = null, legacy = null;
        var extra = new List<WaterOpticsHandle>();
        int baseline = scene.WaterOpticsStats.LiveFields;
        try
        {
            WaterOpticsStats before = scene.WaterOpticsStats;
            var watch = Stopwatch.StartNew();
            authored = Load(recorder, doc, authoredOptions, c);
            result.Milliseconds["AuthoredPreparationIncludingUpload"] = watch.Elapsed.TotalMilliseconds;
            WaterOpticsStats after = scene.WaterOpticsStats;
            result.LiveFieldDelta = after.LiveFields - before.LiveFields;
            result.UploadDelta = after.UploadCount - before.UploadCount;
            result.LiveTextureByteDelta = after.LiveTextureBytes - before.LiveTextureBytes;
            result.LiveRetainedCpuByteDelta = after.LiveRetainedCpuBytes - before.LiveRetainedCpuBytes;
            result.Failures.AddRange(authored.WaterOpticsFailures);
            result.SourceBytes = authored.WaterOpticsSourceBytes;
            watch.Restart();
            legacy = Load(new Scene3DTileWorldScene(scene), doc, legacyOptions, c);
            result.Milliseconds["SceneDepthPreparation"] = watch.Elapsed.TotalMilliseconds;

            TileWorldView a = authored, l = legacy;
            watch.Restart();
            result.Images["authored"] = Render(() =>
            {
                recorder.ClearFrame();
                authoredOptions.DrawWater = true;
                a.Draw(c.Focus);
            });
            result.Milliseconds["AuthoredTwoFrames"] = watch.Elapsed.TotalMilliseconds;
            result.RenderOrigin = scene.RenderOrigin ?? Vector3.Zero;
            result.RenderOriginActive = scene.RenderOriginActive;
            var drawnMeshes = recorder.DrawnMeshes.ToList();
            WaterPlane[] planes = recorder.DrawnWater.ToArray();
            result.Images["scene-depth"] = Render(() => l.Draw(c.Focus));
            result.Images["opaque"] = Render(() =>
            {
                authoredOptions.DrawWater = false;
                a.Draw(c.Focus);
            });
            if (planes.Length > 0 && result.Failures.Count == 0)
                Analyse(c, doc, device, scene, recorder, a, authoredOptions, drawnMeshes, planes, extra, result);
        }
        catch (Exception error)
        {
            result.Error = error;
        }
        finally
        {
            foreach (WaterOpticsHandle handle in extra) scene.UnloadWaterOpticsField(handle);
            legacy?.Dispose();
            authored?.Dispose();
            result.LiveFieldsAfterRelease = scene.WaterOpticsStats.LiveFields - baseline;
        }
        result.Milliseconds["Total"] = total.Elapsed.TotalMilliseconds;
        Write(result);
        return result;
    }

    void Analyse(TileWaterTopologyCase c, TileWorldDocument doc, IGpuDevice device, Scene3D scene,
        TileWorldWaterTopologyRecorder recorder, TileWorldView authored, TileWorldViewOptions authoredOptions,
        List<(GltfMesh Mesh, Matrix4x4 World)> drawnMeshes, WaterPlane[] planes, List<WaterOpticsHandle> extra,
        TileWaterTopologyCapture result)
    {
        float tileSize = doc.TileSize;
        var watch = Stopwatch.StartNew();
        List<TileWaterWorldTriangle> drawn = TileWorldWaterTopologyOracle.Drawn(drawnMeshes, tileSize);
        List<TileWaterWorldTriangle> witness =
            TileWorldWaterTopologyOracle.Witness(c, doc, TileRenderTestData.Catalogs, drawn);
        var allRects = planes.Select(p => TileWaterWorldRect.Of(p)).ToArray();
        result.DrawnWaterTriangles = drawn.Count(t => allRects.Any(r => r.Contains(
            (t.World(0).X + t.World(1).X + t.World(2).X) / 3, (t.World(0).Z + t.World(1).Z + t.World(2).Z) / 3)));
        double witnessShift = c.Witness == TileWaterTopologyWitness.ShiftedBinding ? 1d : 0d;
        var controlPlanes = new List<WaterPlane>();
        var witnessPlanes = new List<WaterPlane>();
        double probeMs = 0;
        foreach (IGrouping<WaterOpticsHandle, WaterPlane> group in planes.GroupBy(p => p.Optics.Field))
        {
            WaterOpticsTriangleField field = recorder.Fields[group.Key];
            Vector3 origin = group.First().Optics.Origin;
            float reference = origin.Y;
            var rects = group.Select(p => TileWaterWorldRect.Of(p)).ToList();
            int matched = 0;
            string? orderError = null;
            try { matched = TileWorldWaterTopologyOracle.VerifySourceOrder(field, origin, drawn, rects); }
            catch (InvalidOperationException error) { orderError = error.Message; }

            TileWaterIndependentField control = TileWorldWaterTopologyOracle.Field(drawn, rects, reference, tileSize);
            WaterOpticsHandle controlHandle = scene.LoadWaterOpticsTriangles(control.Field);
            extra.Add(controlHandle);
            TileWaterIndependentField wrong = witnessShift != 0
                ? TileWorldWaterTopologyOracle.Field(drawn, rects.Select(r => r.Shifted(-witnessShift)).ToList(), reference, tileSize)
                : TileWorldWaterTopologyOracle.Field(witness, rects, reference, tileSize);
            WaterOpticsHandle wrongHandle = scene.LoadWaterOpticsTriangles(wrong.Field);
            extra.Add(wrongHandle);
            Vector3 wrongOrigin = wrong.Origin + new Vector3((float)witnessShift, 0f, 0f);
            foreach (WaterPlane plane in group)
            {
                controlPlanes.Add(Rebind(plane, controlHandle, control.Origin, control.Field.Size));
                witnessPlanes.Add(Rebind(plane, wrongHandle, wrongOrigin, wrong.Field.Size));
            }
            result.Fields.Add(TileWaterFieldReport.Of(field, origin, tileSize, matched, orderError, control.Attempts));

            var probes = Probes(c, field, origin, rects);
            var probeWatch = Stopwatch.StartNew();
            TileWaterGpuSample[] samples = TileWorldWaterTopologyGpuProbe.Query(device, field,
                probes.Select(p => p.Local).ToArray());
            probeMs += probeWatch.Elapsed.TotalMilliseconds;
            for (int i = 0; i < probes.Count; i++)
            {
                (string kind, Vector2 local) = probes[i];
                double x = origin.X + (double)local.X, z = origin.Z + (double)local.Y;
                double height = kind == "miss" ? double.NaN : TileWorldWaterTopologyOracle.Height(drawn, x, z);
                double oracle = reference - height;
                double other = double.NaN;
                if (kind != "miss")
                {
                    try { other = reference - TileWorldWaterTopologyOracle.Height(witness, x - witnessShift, z); }
                    catch (InvalidOperationException) { }
                }
                result.Depths.Add(new(kind, x, z, local, samples[i].Hit, samples[i].Depth, oracle, other,
                    group.First().SurfaceY - height, field.ReconstructionErrorBoundMetres));
            }
        }
        result.Milliseconds["OracleControlAndGpuProbes"] = watch.Elapsed.TotalMilliseconds;
        result.Milliseconds["GpuProbeDispatchAndReadback"] = probeMs;

        result.Images["matched-control"] = Render(() =>
        {
            authoredOptions.DrawWater = false;
            authored.Draw(c.Focus);
            foreach (WaterPlane plane in controlPlanes) scene.DrawWater(plane);
        });
        result.Images["witness"] = Render(() =>
        {
            authoredOptions.DrawWater = false;
            authored.Draw(c.Focus);
            foreach (WaterPlane plane in witnessPlanes) scene.DrawWater(plane);
        });
        result.ControlMaximumRgb = TileWaterTopologyCapture.MaximumRgbDistance(result.Images["authored"], result.Images["matched-control"]);
        result.WitnessMaximumRgb = TileWaterTopologyCapture.MaximumRgbDistance(result.Images["authored"], result.Images["witness"]);
        ProbeImages(c, planes, drawn, result);
    }

    static WaterPlane Rebind(in WaterPlane plane, WaterOpticsHandle handle, Vector3 origin, Vector2 size) =>
        new(plane.CenterX, plane.SurfaceY, plane.CenterZ, plane.HalfExtentX, plane.HalfExtentZ, plane.Look,
            new WaterOpticsBinding(handle, origin, size));

    // A closed lattice over each water rectangle, the case's named points, every interior bin boundary that
    // crosses the water, and two points outside the field that must miss.
    static List<(string Kind, Vector2 Local)> Probes(TileWaterTopologyCase c, WaterOpticsTriangleField field,
        Vector3 origin, List<TileWaterWorldRect> rects)
    {
        var result = new List<(string, Vector2)>();
        var seen = new HashSet<Vector2>();
        void Add(string kind, double x, double z)
        {
            var local = new Vector2((float)(x - origin.X), (float)(z - origin.Z));
            double wx = origin.X + (double)local.X, wz = origin.Z + (double)local.Y;
            if (rects.Any(r => r.Contains(wx, wz)) && seen.Add(local)) result.Add((kind, local));
        }
        foreach (TileWaterWorldRect r in rects)
            for (double z = r.MinZ; z <= r.MaxZ + 1e-9; z += c.LatticeSpacing)
                for (double x = r.MinX; x <= r.MaxX + 1e-9; x += c.LatticeSpacing) Add("lattice", x, z);
        foreach (Vector2 p in c.Probes) Add("named", p.X, p.Y);
        TileWaterWorldRect first = rects[0];
        double middleZ = (first.MinZ + first.MaxZ) / 2, middleX = (first.MinX + first.MaxX) / 2;
        for (int i = 1; i < field.BinColumns; i++) Add("bin-edge", origin.X + (double)(field.Size.X * i / field.BinColumns), middleZ);
        for (int i = 1; i < field.BinRows; i++) Add("bin-edge", middleX, origin.Z + (double)(field.Size.Y * i / field.BinRows));
        result.Add(("miss", new Vector2(field.Size.X + 0.5f, field.Size.Y / 2)));
        result.Add(("miss", new Vector2(-0.5f, field.Size.Y / 2)));
        return result;
    }

    void ProbeImages(TileWaterTopologyCase c, WaterPlane[] planes, List<TileWaterWorldTriangle> drawn,
        TileWaterTopologyCapture result)
    {
        var points = new Dictionary<string, (double X, double Z, double Depth)>();
        float surface = planes[0].SurfaceY;
        for (int i = 0; i < c.Probes.Length; i++)
            points.Add($"named-{i}", (c.Probes[i].X, c.Probes[i].Y, surface - TileWorldWaterTopologyOracle.Height(drawn, c.Probes[i].X, c.Probes[i].Y)));
        int lattice = 0;
        foreach (TileWaterDepthProbe probe in result.Depths)
            if (probe.Kind == "lattice" && probe.PhysicalDepth >= DeepMetres)
                points.Add($"deep-{lattice++}", (probe.WorldX, probe.WorldZ, probe.PhysicalDepth));
        var pixels = new Dictionary<string, Vector2>();
        foreach ((string name, (double x, double z, double _)) in points)
            if (_camera!.WorldToScreen(new Vector3((float)x, surface, (float)z), Size, Size, out Vector2 pixel)
                && pixel.X >= 2 && pixel.Y >= 2 && pixel.X <= Size - 3 && pixel.Y <= Size - 3)
                pixels.Add(name, pixel);
        WaterOpticsImage Probe(string image) => new(result.Images[image].Rgba, Size, Size, pixels);
        WaterOpticsImage authored = Probe("authored"), scene = Probe("scene-depth"), opaque = Probe("opaque");
        WaterOpticsImage control = Probe("matched-control"), witness = Probe("witness");
        foreach ((string name, Vector2 pixel) in pixels)
        {
            float influence = Vector3.Distance(authored.Sample(name), opaque.Sample(name));
            if (name.StartsWith("deep-", StringComparison.Ordinal))
            {
                result.ProjectedDeepProbes++;
                if (influence > VisibleInfluence) result.VisibleDeepProbes++;
                continue;
            }
            (double x, double z, double depth) = points[name];
            result.ImageProbes.Add(new(name, x, z, pixel, depth, influence,
                Vector3.Distance(scene.Sample(name), opaque.Sample(name)),
                Vector3.Distance(authored.Sample(name), control.Sample(name)),
                Vector3.Distance(authored.Sample(name), witness.Sample(name))));
        }
    }

    TileWorldView Load(ITileWorldScene target, TileWorldDocument doc, TileWorldViewOptions options, TileWaterTopologyCase c)
    {
        var dispatcher = new ManualTileBuildDispatcher();
        var view = new TileWorldView(target, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(), options,
            new TileWorldBuildQueueOptions
            {
                MaxConcurrentBuilds = 8, MaxFullGroundAppliesPerPump = 8, MaxCoarseGroundAppliesPerPump = 8,
            }, dispatcher);
        foreach ((RegionCoord region, TileRegionResidencyState? residency) in c.Loads)
        {
            if (residency is { } state) view.LoadRegion(region, state);
            else view.LoadRegion(region);
        }
        if (!c.Profiled) return view;
        for (int i = 0; i < 4; i++)
        {
            // Each pump sits in a begun, never rendered frame, so its queued draws are discarded.
            _scene!.Begin();
            view.Draw(c.Focus);
            dispatcher.RunAll();
        }
        _scene!.Begin();
        view.Draw(c.Focus);
        return view;
    }

    WaterOpticsImage Render(Action draw)
    {
        IGpuDevice device = Device();
        Scene3D scene = _scene!;
        for (int frame = 0; frame < Frames; frame++)
        {
            scene.Begin();
            scene.EffectTimeSeconds = 0f;
            draw();
            scene.PrepareFrame(Size, Size);
            using (GpuRecording.Open(device, _commands!, "TileWorldWaterTopologyScene.Render"))
                scene.RenderInternal(_commands!, Size, Size, _framebuffer!);
            device.Submit(_commands!);
            device.WaitForIdle();
            device.Present();
        }
        return new WaterOpticsImage(GpuReadback.ToRgba(device, _target!, Size, Size), Size, Size,
            new Dictionary<string, Vector2>());
    }

    void Configure(TileWaterTopologyCase c)
    {
        _camera = new FlyCamera3D { AspectRatio = 1f, NearPlane = 0.02f, FarPlane = c.FarPlane, Position = c.Eye };
        Vector3 direction = Vector3.Normalize(c.Target - c.Eye);
        _camera.Yaw = MathF.Atan2(direction.X, direction.Z);
        _camera.Pitch = MathF.Asin(direction.Y);
        _scene!.CameraOverride = _camera;
        _scene.CameraCut();
        // The automatic latch reduces the distant case by its 2048 m anchor, exactly as a game frame does.
        _scene.RenderOrigin = null;
    }

    void Write(TileWaterTopologyCapture result)
    {
        Directory.CreateDirectory(DirectoryPath);
        string[] order = ["authored", "scene-depth", "opaque", "matched-control", "witness"];
        WriteSheet(result.Name + "-contactsheet", order.Where(result.Images.ContainsKey).Select(n => result.Images[n]).ToArray());
        File.WriteAllText(Path.Combine(DirectoryPath, result.Name + ".json"), JsonSerializer.Serialize(new
        {
            Metrics = result.Metrics(Backend, DeviceName),
            ContactSheetOrder = order.Where(result.Images.ContainsKey).ToArray(),
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true, NumberHandling =
            System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    }

    internal void WriteSheet(string name, IReadOnlyList<WaterOpticsImage> images)
    {
        if (images.Count == 0) return;
        Directory.CreateDirectory(DirectoryPath);
        var rgba = new byte[images.Count * Size * Size * 4];
        for (int index = 0; index < images.Count; index++)
            for (int y = 0; y < Size; y++)
                Buffer.BlockCopy(images[index].Rgba, y * Size * 4, rgba, (y * images.Count * Size + index * Size) * 4, Size * 4);
        PngWriter.Save(Path.Combine(DirectoryPath, name + ".png"), rgba, images.Count * Size, Size);
    }

    IGpuDevice Device()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_gpu is not null) return _gpu.GpuDevice;
        try
        {
            _gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice device = _gpu.GpuDevice;
            _target = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                Size, Size, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _framebuffer = device.Factory.CreateFramebuffer(null, _target);
            _commands = device.Factory.CreateCommandList();
            _scene = new Scene3D(device, _framebuffer.Outputs);
            _scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
            _scene.Post.Starfield = false;
            _scene.Post.Sky.Enabled = false;
            _scene.Post.Background = BackgroundMode.Solid;
            _scene.Post.BackgroundColor = new Color(0.04f, 0.05f, 0.08f, 1f);
            _scene.Post.LightColor = new Color(0f, 0f, 0f, 1f);
            _scene.Post.FillLightColor = new Color(0f, 0f, 0f, 1f);
            _scene.Post.AmbientColor = Color.White;
            return device;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gpu?.GpuDevice.WaitForIdle();
        _commands?.Dispose();
        _scene?.Dispose();
        _framebuffer?.Dispose();
        _target?.Dispose();
        _gpu?.Dispose();
    }
}
