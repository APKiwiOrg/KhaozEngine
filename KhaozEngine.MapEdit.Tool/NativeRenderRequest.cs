using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapEdit;

public sealed record NativeFrame(short X, short Z);
public sealed record NativeVector(double X, double Y, double Z);
public sealed record NativeLocalVector(float X, float Y, float Z);
public sealed record NativeFramePoint(NativeFrame Frame, NativeLocalVector Local)
{
    public MapFramePoint ToPoint() => new(new WorldFrame(Frame.X, Frame.Z), new((float)Local.X, (float)Local.Y, (float)Local.Z));
    public static NativeFramePoint From(MapFramePoint point) => new(new(point.Frame.X, point.Frame.Z),
        new(point.Local.X, point.Local.Y, point.Local.Z));
}
public sealed record NativeScope(NativeFrame Frame, NativeVector Min, NativeVector Max, IReadOnlyList<string>? SpaceIds,
    string Channels, double MotionPaddingMetres)
{
    public MapSceneScope ToScope() => new(new(new WorldFrame(Frame.X, Frame.Z), new((float)Min.X, (float)Min.Z),
        new((float)Max.X, (float)Max.Z), (float)Min.Y, (float)Max.Y, Enum.GetValues<MapSurfaceRole>(), SpaceIds, new()),
        new(Min.X + Frame.X * 128d, Min.Y, Min.Z + Frame.Z * 128d, Max.X + Frame.X * 128d, Max.Y, Max.Z + Frame.Z * 128d),
        NativeJson.Flags(Channels), MotionPaddingMetres);
    public static NativeScope From(MapSceneScope scope)
    {
        var f = scope.Surfaces.Frame; var b = scope.DrawBounds;
        return new(new(f.X, f.Z), new(b.MinX - f.X * 128d, b.MinY, b.MinZ - f.Z * 128d),
            new(b.MaxX - f.X * 128d, b.MaxY, b.MaxZ - f.Z * 128d), scope.Surfaces.SpaceIds, scope.Channels.ToString(), scope.MotionPaddingMetres);
    }
}
public sealed record NativeFraming(NativeFramePoint Eye, NativeFramePoint Target, bool Orthographic,
    float VerticalSizeOrFovDegrees, int Width, int Height, float NearPlane, float FarPlane)
{
    public MapSnapshotFraming ToFraming() => new(Eye.ToPoint(), Target.ToPoint(), Orthographic, VerticalSizeOrFovDegrees, Width, Height, NearPlane, FarPlane);
    public static NativeFraming From(MapSnapshotFraming f) => new(NativeFramePoint.From(f.Eye), NativeFramePoint.From(f.Target),
        f.Orthographic, f.VerticalSizeOrFovDegrees, f.Width, f.Height, f.NearPlane, f.FarPlane);
}

/// <summary>Closed primitive request. Parsing and domain checks precede all host and device access.</summary>
public sealed record NativeRenderRequest(int SchemaVersion, NativeFraming Framing, NativeScope Scope,
    NativeFramePoint Observer, NativeFramePoint? Focus, string? ObserverSpaceId, string RoofMode, double TimeSeconds,
    int WindSeed, string Aa, string StyleId, string Requirement, int WarmupFrames)
{
    public static NativeRenderRequest Read(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            NativeJson.Object(root, "schemaVersion framing scope observer observerSpaceId roofMode timeSeconds windSeed aa styleId requirement warmupFrames", "focus");
            NativeJson.Object(root.GetProperty("framing"), "eye target orthographic verticalSizeOrFovDegrees width height nearPlane farPlane");
            NativeJson.Object(root.GetProperty("scope"), "frame min max spaceIds channels motionPaddingMetres");
            void Frame(JsonElement frame)
            {
                NativeJson.Object(frame, "x z");
                foreach (string axis in new[] { "x", "z" })
                    if (!frame.GetProperty(axis).TryGetInt16(out _)) throw new ArgumentException("InvalidFrame");
            }
            void Vector(JsonElement v)
            {
                NativeJson.Object(v, "x y z");
                foreach (string axis in new[] { "x", "y", "z" })
                    if (!v.GetProperty(axis).TryGetDouble(out double value) || !double.IsFinite(value) || !float.IsFinite((float)value))
                        throw new ArgumentException("InvalidCoordinate");
            }
            void Point(JsonElement v) { NativeJson.Object(v, "frame local"); Frame(v.GetProperty("frame")); Vector(v.GetProperty("local")); }
            Point(root.GetProperty("framing").GetProperty("eye")); Point(root.GetProperty("framing").GetProperty("target"));
            Point(root.GetProperty("observer"));
            if (root.TryGetProperty("focus", out var focus) && focus.ValueKind != JsonValueKind.Null) Point(focus);
            var scope = root.GetProperty("scope"); Frame(scope.GetProperty("frame")); Vector(scope.GetProperty("min")); Vector(scope.GetProperty("max"));
            var result = JsonSerializer.Deserialize<NativeRenderRequest>(json, NativeJson.Options)!;
            result.Validate(); return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or OverflowException or KeyNotFoundException or FormatException)
        { throw new ArgumentException("InvalidNativeRequest", nameof(json), error); }
    }
    void Validate()
    {
        var s = Scope; var f = Framing;
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(StyleId) || WarmupFrames < 0 || !double.IsFinite(TimeSeconds) ||
            s.Min.X >= s.Max.X || s.Min.Z >= s.Max.Z || s.Min.Y > s.Max.Y ||
            !double.IsFinite(s.MotionPaddingMetres) || s.MotionPaddingMetres < 0 ||
            f.Width < 1 || f.Height < 1 || !float.IsFinite(f.NearPlane) || !float.IsFinite(f.FarPlane) ||
            f.NearPlane <= 0 || f.FarPlane <= f.NearPlane || !float.IsFinite(f.VerticalSizeOrFovDegrees) ||
            f.VerticalSizeOrFovDegrees <= 0 || (!f.Orthographic && f.VerticalSizeOrFovDegrees >= 180) ||
            f.Eye.ToPoint().ExactWorldXz() == f.Target.ToPoint().ExactWorldXz() && f.Eye.Local.Y == f.Target.Local.Y ||
            s.SpaceIds is not null && (s.SpaceIds.Any(string.IsNullOrWhiteSpace) || s.SpaceIds.Distinct(StringComparer.Ordinal).Count() != s.SpaceIds.Count) ||
            ObserverSpaceId is not null && string.IsNullOrWhiteSpace(ObserverSpaceId)) throw new ArgumentException("InvalidNativeDomain");
        NativeJson.Enum<MapRoofMode>(RoofMode); NativeJson.Enum<MapCaptureAa>(Aa); NativeJson.Enum<MapCaptureRequirement>(Requirement); NativeJson.Flags(s.Channels);
    }
    public MapSnapshotRequest Resolve(MapNativeRenderContext context, MapCaptureRoute route)
    {
        Validate();
        if (context.StyleId != StyleId) throw new ArgumentException("NativeStyleMismatch");
        if (context.Source.ClosureHash != context.Closure.Hash || context.Extents.ClosureHash != context.Closure.Hash)
            throw new ArgumentException("NativeClosureMismatch");
        var source = context.Source.WithRenderExtents(context.Extents).Source ?? throw new ArgumentException("NativeExtentsMismatch");
        if (route == MapCaptureRoute.TopDown && !Framing.Orthographic) throw new ArgumentException("TopDownRequiresOrthographic");
        return new(context.Closure, source, Scope.ToScope(), Framing.ToFraming(), Observer.ToPoint(), ObserverSpaceId,
            NativeJson.Enum<MapRoofMode>(RoofMode), TimeSeconds, WindSeed, StyleId,
            new(context.RenderPolicy, context.Lighting, NativeJson.Enum<MapCaptureAa>(Aa), WarmupFrames),
            NativeJson.Enum<MapCaptureRequirement>(Requirement), route, Focus?.ToPoint())
        { PreviewWindowLimit = context.PreviewWindowLimit };
    }
}
internal static class NativeJson
{
    internal static readonly JsonSerializerOptions Options = Create();
    static JsonSerializerOptions Create()
    {
        var options = MapJsonTextOptions.CreateSerializer(false);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = false; options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.IncludeFields = true;
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)); return options;
    }
    internal static void Object(JsonElement element, string required, string optional = "")
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("InvalidObject");
        var needed = required.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var allowed = needed.Concat(optional.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in element.EnumerateObject())
            if (!allowed.Contains(p.Name) || !seen.Add(p.Name)) throw new ArgumentException("UnknownOrDuplicateField");
        if (!needed.IsSubsetOf(seen)) throw new ArgumentException("MissingField");
    }
    internal static T Enum<T>(string value) where T : struct, Enum => System.Enum.TryParse<T>(value, false, out var parsed) &&
        System.Enum.IsDefined(parsed) && parsed.ToString() == value ? parsed : throw new ArgumentException("InvalidEnum");
    internal static MapSceneChannels Flags(string value)
    {
        var pieces = value.Split(", ", StringSplitOptions.None);
        if (pieces.Length == 0 || pieces.Any(p => !System.Enum.GetNames<MapSceneChannels>().Contains(p, StringComparer.Ordinal)) ||
            !System.Enum.TryParse<MapSceneChannels>(value, false, out var parsed) || (parsed & ~MapSceneChannels.All) != 0)
            throw new ArgumentException("InvalidChannels");
        return parsed;
    }
}
