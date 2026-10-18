using System;

namespace KhaozEngine.Render3D;

/// <summary>Marks the numerical graph reproduced by coverage device kernels.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class Binary32KernelAttribute : Attribute { }

/// <summary>Marks the hand-reviewed home of the binary32 operation inventory.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class Binary32ArithmeticAttribute : Attribute { }
