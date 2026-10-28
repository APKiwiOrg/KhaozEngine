using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.Windowing;

// Runs only during detached report export. The sampled buffer retains value types, never this object graph.
internal sealed class FrameDiagnosticsInputSerialization : JsonConverter<ShadowStageProbeInputs>
{
    public FrameDiagnosticsInputSerialization() { }

    public override ShadowStageProbeInputs Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("Private input observations are export-only.");

    public override void Write(Utf8JsonWriter writer, ShadowStageProbeInputs value, JsonSerializerOptions options)
    {
        var view = value.View;
        var fits = value.Fits;
        JsonSerializer.Serialize(writer, new
        {
            request = value.Request,
            sceneFrameIndex = value.SceneFrameIndex,
            value.PrefixCount,
            value.ViewCount,
            value.CascadeObservationCount,
            value.CameraCount,
            value.LightCount,
            value.DecisionCount,
            value.Availability,
            value.Complete,
            matrixOrder = "row-major-M11-M44",
            prefixEffectTimeWords = Words(value.PrefixEffectTimeSeconds),
            view = new
            {
                viewWords = MatrixWords(view.View),
                projectionWords = MatrixWords(view.Projection),
                viewProjectionWords = MatrixWords(view.ViewProjection),
                absoluteViewProjectionWords = MatrixWords(view.AbsoluteViewProjection),
                renderOriginWords = VectorWords(view.RenderOrigin),
                view.Width,
                view.Height,
                jitterPixelsWords = Words(view.JitterPixels.X, view.JitterPixels.Y),
            },
            camera = new { eyeWords = VectorWords(value.Camera.Eye), forwardWords = VectorWords(value.Camera.Forward) },
            light = new
            {
                rawWords = VectorWords(value.Light.Raw),
                normalizedWords = VectorWords(value.Light.Normalized),
                selectedWords = VectorWords(value.Light.Selected),
            },
            fits = new
            {
                fits.Count,
                fits.Resolution,
                splitWords = Words(fits.Splits.X, fits.Splits.Y, fits.Splits.Z, fits.Splits.W),
                absoluteFitWords = new[] { MatrixWords(fits.Fit0), MatrixWords(fits.Fit1), MatrixWords(fits.Fit2), MatrixWords(fits.Fit3) },
            },
            decision = value.Decision,
        }, options);
    }

    static uint[] Words(params float[] values) => Array.ConvertAll(values, BitConverter.SingleToUInt32Bits);
    static uint[] VectorWords(Vector3 value) => Words(value.X, value.Y, value.Z);
    static uint[] MatrixWords(Matrix4x4 m) => Words(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
}
