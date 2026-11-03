using System.Collections.Generic;
using System.IO;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Literals captured from the v1 campaign code before the typed schedule table existed. Later campaigns add
// recipes beside these and must never move them. ShaderSha256 is pinned by the shader identity tests because
// legitimate shader work on main moves it.
public sealed class RendererPerfV1IdentityGoldenTests
{
    // Portable digests: file bytes, recipe, settings, geometry and assets.
    [Theory]
    [InlineData("empty",
        "61f6349add9773984ab94f8091e37a6a9728f761f8cf27fd3f6d470bddc336b5",
        "a008817d6b73ecd4aad1ac74e693a61a34d40844359eae3932ac2a58e5b915da",
        "eea727a2f8338cd28b5c2a55b4de77c8cebca7e1fc08b2a1b74d2a6c6328871c",
        "4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945",
        "4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945")]
    [InlineData("village",
        "9ba633721f51220ed7bd21fccb9966bd5e1af729020c4eb560322eade01106f8",
        "3caa143955578cb0505615593d45af7347c4547fbc48861b8b58d1edc8de5eb0",
        "42e03f9674742023bdc5707d6fbfe12a8bcc9c697fb8ff3e99c95bd80e2c00a9",
        "7d3fa9a67fc4abce8c70de4be065bc4950cb5f832890585d65b73251585d2cd3",
        "99dc87de2047b8b4e06ed9a4aa8ace50f66267abc3ec134bc5acfdd7341eaa9b")]
    [InlineData("outdoors",
        "7bc634ed57d45bc3dac6301f546d0b246852688652eea320f20badfe7d7ddfb0",
        "4c4c72ce0586e35112cfa502fa8dab2f545360897d9c67db5b93a29e66b6236f",
        "af65b9c3e6237fe32492962a662a81da2c72ae787bcc5da3207125005f6fb686",
        "7716a31991470815735af23855bca7435f4bd2c012761db8189806a1e920c17a",
        "e72c2b340d0c7bd66c83cac45f400748f143146672c663ed7173eb2577bcfafa")]
    [InlineData("crowd",
        "2b72cbd302a6e65c755b6dad565cb8f8479cdf1783d4c5cf68b070b3da2f21b3",
        "dd702040fa4f868a9377cbf09c4eb7ff0fc57d54adb856fac2fec3b4af180f0b",
        "fa254848b4d8b245d5fdb16e6e7617d00f2c0e6ea22336f123ae2da0434cb2a2",
        "90732a30384a0e9554ea017ce81a2c403c3b4a421f90035fe4a4b82b786df6e9",
        "99dc87de2047b8b4e06ed9a4aa8ace50f66267abc3ec134bc5acfdd7341eaa9b")]
    [InlineData("effects-ui",
        "754bba249a2ba11ff6d10131f08db3c883a31223b3f584479074aaf1bb1947d9",
        "83f32fba53c54fecdadf10f4fd082528406ac761f98acfd0609df7950cc7ce86",
        "9ea38f9ed7d788098bf5d2ee70e8d39c0972762eb821758e9686a3d79573bf6c",
        "1227d278786bcb1a5820c95be09e4feb6ee2ae0268b26aa9ffa0a08cd6ffb647",
        "ef723047c3daf64c09e3abbbb1e914ee15af010984a81a07698cfc07c34897ba")]
    public void V1_recipe_files_and_identities_are_pinned(string id, string file, string recipeSha, string settings,
        string geometry, string asset)
    {
        string actualFile = RendererPerfRecipe.Hash(File.ReadAllBytes(RendererPerfRecipe.RecipePath(id)));
        using var recipe = RendererPerfRecipe.Create(id);
        var f = recipe.Facts;
        Assert.Equal(file, actualFile);
        Assert.Equal(recipeSha, f.RecipeSha256);
        Assert.Equal(settings, f.SettingsSha256);
        Assert.Equal(geometry, f.GeometrySha256);
        Assert.Equal(asset, f.AssetSha256);
    }

    // The frame sequence hashes camera matrices, so each platform family has its own frozen value.
    static readonly Dictionary<string, Dictionary<string, string>> Sequences = new()
    {
        ["empty"] = new()
        {
            ["osx-arm64"] = "2cd914f6deb02f61491c43908e1797ecefb7bab883c53bd610e7ab100f80a3f0",
            ["linux-x64"] = "2cd914f6deb02f61491c43908e1797ecefb7bab883c53bd610e7ab100f80a3f0",
            ["win-x64"] = "2cd914f6deb02f61491c43908e1797ecefb7bab883c53bd610e7ab100f80a3f0",
        },
        ["village"] = new()
        {
            ["osx-arm64"] = "f256612a28ff007f88a8293b655c94e75ee5da2f7928fc1754237eefbf53559d",
            ["linux-x64"] = "d067b04499beacd3c233ace61f10cfec76e96e3619a71daaad416ba9df462e61",
            ["win-x64"] = "d067b04499beacd3c233ace61f10cfec76e96e3619a71daaad416ba9df462e61",
        },
        ["outdoors"] = new()
        {
            ["osx-arm64"] = "03f2bcef1732eeba873c1d364de6fa684cd14c32e2a7347d1be8655090543be8",
            ["linux-x64"] = "1e26eecfe276060e88515acbe8ba9e8e6bdd702edc07c87b822b9f1d86800a00",
            ["win-x64"] = "d1f3298b1047143d9e781d147a833f1b74e7df5f419a520a688f68f84a4446b2",
        },
        ["crowd"] = new()
        {
            ["osx-arm64"] = "e6d2f9f6cd1d078d07fa2efeede251b3ac9e5260430304b638f0d4cfafb83cad",
            ["linux-x64"] = "3daf122134356bac75e6c4fbaaf3897002d375b26c53cd5afe0c1bd08d96fadc",
            ["win-x64"] = "52c6699c4c1d2d9d09b59117e4bcc9954fbd360b241e5f6d52a804b01545376f",
        },
        ["effects-ui"] = new()
        {
            ["osx-arm64"] = "33b5cf24dcda5f1054a1606f4cf31e95f4d9fc657eed1c6f71484098b9b435fb",
            ["linux-x64"] = "463d8ea342287f8254ae7412f68ff79291f1718cacd6b6150ce5537ed2ea1a3f",
            ["win-x64"] = "be6bbe9e44f5b11d356563660cecd9c4abcaccd375112bd242bc46f54f7b47a1",
        },
    };

    [Theory]
    [InlineData("empty")]
    [InlineData("village")]
    [InlineData("outdoors")]
    [InlineData("crowd")]
    [InlineData("effects-ui")]
    public void V1_recipe_sequence_is_pinned_for_this_platform_family(string id)
    {
        using var recipe = RendererPerfRecipe.Create(id);
        Assert.True(Sequences[id].TryGetValue(recipe.Facts.PlatformFamily, out string? expected),
            $"No pinned v1 sequence for {recipe.Facts.PlatformFamily}.");
        Assert.Equal(expected, recipe.Facts.SequenceSha256);
    }
}
