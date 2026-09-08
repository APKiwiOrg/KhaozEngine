namespace KhaozEngine.TileWorld.Netcode;

// The command kind becomes a carried state domain while a route spans later Continue commands. Kept here so the
// simulator and server action queue cannot grow separate mappings between the two public enums.
static class TileInteractionTarget
{
    public static TileInteractionDomain DomainOf(TileCommandKind kind) =>
        kind == TileCommandKind.InteractEntity
            ? TileInteractionDomain.Entity
            : TileInteractionDomain.AuthoredObject;
}
