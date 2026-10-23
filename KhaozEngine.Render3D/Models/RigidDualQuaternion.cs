using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D
{
    /// <summary>Sequential 32-byte real/dual storage. Construction alone does not validate a rigid palette.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct RigidDualQuaternion
    {
        public Vector4 Real { get; }
        public Vector4 Dual { get; }

        public RigidDualQuaternion(Vector4 real, Vector4 dual)
        {
            Real = real;
            Dual = dual;
        }
    }
}
