namespace KhaozEngine.Render3D.Diagnostics;

/// <summary>A stable pass name and its occurrence within one recording.</summary>
public readonly record struct RenderPassKey(string Id, int Occurrence = 0);
