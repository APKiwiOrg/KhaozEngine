namespace KhaozEngine.TileWorld.Netcode;

// InteractTarget already rides every predicted and replicated movement state as one long. Entity interactions use
// the otherwise-unused negative half of that field so the simulator remembers which resolver owns the target while
// a route spans later Continue commands. Net ids are always positive because NetIdAllocator reserves the sign bit.
static class TileInteractionTarget
{
    public static long Encode(TileCommandKind kind, long target) =>
        kind == TileCommandKind.InteractEntity ? -target : target;

    public static bool IsEntity(long encoded) => encoded < 0;

    public static long Decode(long encoded) => encoded < 0 ? -encoded : encoded;
}
