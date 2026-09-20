using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary><see cref="SkySettings.MaxDiscs"/> vec4s in a row: one std140 <c>vec4[8]</c> member of the sky and
    /// water uniform blocks.</summary>
    [InlineArray(SkySettings.MaxDiscs)]
    internal struct SkyDiscVec4s
    {
        Vector4 _element0;
    }

    /// <summary>
    /// The one place that decides WHICH discs a frame draws, so the sky pass and the water's reflected sky can never
    /// disagree about it: the primary disc (when <see cref="SkySettings.SunEnabled"/>) and then
    /// <see cref="SkySettings.ExtraDiscs"/> in list order, invisible ones dropped, capped at
    /// <see cref="SkySettings.MaxDiscs"/>. Discs blend in that order, so a later disc goes over an earlier one.
    /// </summary>
    internal static class SkyDiscs
    {
        /// <summary>Fill <paramref name="into"/> with this frame's discs, directions normalized, and return how many.
        /// The primary disc points at <see cref="SkySettings.ResolveSunDirection"/>, so it follows the key light
        /// unless <see cref="SkySettings.SunDirectionOverride"/> says otherwise.</summary>
        public static int Resolve(SkySettings sky, Vector3 lightDirection, Span<SkyDisc> into)
        {
            int count = 0;
            if (sky.SunEnabled && into.Length > 0)
            {
                into[count++] = new SkyDisc
                {
                    Direction = sky.ResolveSunDirection(lightDirection),
                    Color = sky.SunColor,
                    Radius = sky.SunRadius,
                    HaloStrength = sky.HaloStrength,
                    HaloFalloff = sky.HaloFalloff,
                };
            }
            foreach (SkyDisc extra in sky.ExtraDiscs)
            {
                if (count >= into.Length) break;
                if (extra.Color.A <= 0f) continue;
                SkyDisc disc = extra;
                disc.Direction = extra.Direction.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(extra.Direction);
                into[count++] = disc;
            }
            return count;
        }

        /// <summary>The same discs laid out for the water block, where a disc is a DIRECTION (the reflected view
        /// ray has no screen position). Each disc's opacity rides in its colour alpha, so a body the sky has
        /// dissolved is dissolved in the sea too.</summary>
        public static int PackForWater(SkySettings sky, Vector3 lightDirection,
            out SkyDiscVec4s color, out SkyDiscVec4s direction, out SkyDiscVec4s halo)
        {
            color = default;
            direction = default;
            halo = default;
            Span<SkyDisc> discs = stackalloc SkyDisc[SkySettings.MaxDiscs];
            int count = Resolve(sky, lightDirection, discs);
            for (int i = 0; i < count; i++)
            {
                color[i] = discs[i].Color;
                direction[i] = new Vector4(discs[i].Direction, discs[i].Radius);
                halo[i] = new Vector4(discs[i].HaloStrength, discs[i].HaloFalloff, 0f, 0f);
            }
            return count;
        }
    }
}
