using System.Collections.Generic;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// String ids for the engine's core UI icon set (registered into an <see cref="IconAtlas"/> by
    /// <see cref="IconAtlas.Bake"/>). Games register their own ids alongside these via
    /// <see cref="IconAtlas.Register"/>. Outline style, single-colour, tinted at draw time.
    /// </summary>
    public static class Icons
    {
        public const string Coin = "core.coin";
        public const string Heart = "core.heart";
        public const string Skull = "core.skull";
        public const string Crosshair = "core.crosshair";
        public const string Gear = "core.gear";
        public const string Play = "core.play";
        public const string Pause = "core.pause";
        public const string Close = "core.close";
        public const string Check = "core.check";
        public const string Plus = "core.plus";
        public const string Minus = "core.minus";
        public const string ChevronLeft = "core.chevron_left";
        public const string ChevronRight = "core.chevron_right";
        public const string ChevronUp = "core.chevron_up";
        public const string ChevronDown = "core.chevron_down";

        /// <summary>All core ids in atlas-cell order (row-major). Length drives the atlas grid.</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            Coin, Heart, Skull, Crosshair, Gear, Play, Pause, Close,
            Check, Plus, Minus, ChevronLeft, ChevronRight, ChevronUp, ChevronDown,
        };
    }
}
