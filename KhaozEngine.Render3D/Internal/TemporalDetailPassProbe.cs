namespace KhaozEngine.Render3D.Internal;

// Scratch capture only. A renderer without this probe keeps the shipped resources and pass topology.
internal readonly record struct TemporalDetailPassProbe(bool AttachDepth, bool AttachVisibility, bool ArrayStore);
