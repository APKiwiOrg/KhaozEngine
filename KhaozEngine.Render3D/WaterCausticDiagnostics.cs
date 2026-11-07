namespace KhaozEngine.Render3D;

/// <summary>Queued caustic opt-ins and the bodies and atlas layers selected by the latest frame plan.</summary>
/// <param name="OptedInPlanes">All queued bodies with the resolved opt-in enabled, including zero strength.</param>
/// <param name="DrawnPlanes">Bodies assigned a map layer.</param>
/// <param name="FftPlanes">Opted-in bodies using a non-procedural wave source, which never receive a layer.</param>
/// <param name="OverflowPlanes">Participants meeting the window that could not fit in four layers.</param>
/// <param name="Layers">Map layers used by the plan.</param>
public readonly record struct WaterCausticDiagnostics(int OptedInPlanes, int DrawnPlanes, int FftPlanes,
    int OverflowPlanes, int Layers);
