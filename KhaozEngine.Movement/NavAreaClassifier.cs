using System.Numerics;

namespace KhaozEngine.Movement;

/// <summary>Assigns caller-defined area bits to an absolute standable feet position during capture.
/// The callback runs sequentially and must leave the physics origin and statics unchanged.</summary>
public delegate uint NavAreaClassifier(Vector3 absoluteFeetPosition);
