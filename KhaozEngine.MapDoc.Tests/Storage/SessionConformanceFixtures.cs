using System.Globalization;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Storage;

internal static class SessionConformanceFixtures
{
    internal static MapDocument OnePatch() => SurfaceStorageFixtures.FlatPatches(1);

    internal static MapDocument DeletedSeedAndResidentSeed(string dir)
    {
        MapDocument doc = OnePatch();
        var far = new MapPatchKey("ground", 300, 0);
        doc.Surfaces.Patches.Add(far, SurfaceStorageFixtures.FlatPatch(far, 1000));
        MapDocumentFile.SaveTiled(doc, dir);
        MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
        window.Surfaces.Patches.Remove(new("ground", 0, 0));
        var resident = new MapPatchKey("ground", 1, 0);
        window.Surfaces.Patches.Add(resident, SurfaceStorageFixtures.FlatPatch(resident, 1000));
        return window;
    }

    internal static MapDocument ManyExcludedRefs(int count)
    {
        MapDocument doc = ManyRefs(count, MapSurfaceRole.Ceiling);
        if (count != 0)
        {
            var key = new MapPatchKey(doc.Surfaces.Refs[0].Id, 0, 0);
            doc.Surfaces.Patches.Add(key, SurfaceStorageFixtures.FlatPatch(key, 1000));
        }
        return doc;
    }

    internal static MapDocument ManyEmptyRefs(int count) => ManyRefs(count, MapSurfaceRole.SupportFloor);

    static MapDocument ManyRefs(int count, MapSurfaceRole role)
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(0);
        MapSurfaceRef template = doc.Surfaces.Refs[0];
        doc.Surfaces.Refs.Clear();
        for (int i = 0; i < count; i++)
            doc.Surfaces.Refs.Add(template with
            {
                Id = "session-ref-" + i.ToString("D4", CultureInfo.InvariantCulture),
                Role = role,
            });
        return doc;
    }
}
