using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>Committed cross-platform pins over the invented <see cref="FoliageFixtures.Pins"/> fixture: a negative-origin,
/// non-square raster on a twisted PositiveZ ground with cut and rotated overlays, NoDraw and Blocked cells, subdivided
/// boundary edges, a yawed and scaled Solid owner and a deck posed at a non-quarter-turn yaw. Every platform compares the
/// same literals. Keys, decisions, models, materials, faces and the scale and rank bits compare exactly, positions within
/// 0.00001 m per component and orientations within 0.000001 radians. The literals were captured once from the generator
/// and are checked independently against the channel arithmetic, never regenerated to bless a mismatch.</summary>
public sealed class FoliagePlatformPinsTests
{
    const float PositionTolerance = 0.00001f;
    const double OrientationTolerance = 0.000001;
    const int Seed = 41;
    const float ScaleMin = 0.85f, ScaleMax = 1.25f;

    // The released channel salts in Model, Scale and Rank order, copied so the check never calls the evaluator.
    const ulong ModelSalt = 0xA4FBCB9528163E09UL, ScaleSalt = 0x9C3C8D7E5F2A61B4UL, RankSalt = 0xE2A86149B7D30C5FUL;

    // One header per layer, then one row per candidate the chunk owns on the raster, in generation order. A header holds the
    // layer, status, candidate count and reason counts. A row holds the lattice address, support owner and reason, and
    // an accepted row adds the model, scale and rank bits, position and orientation bits, visible material and face.
    const string Pins = """
        pin-cover|Complete|4489|Accepted=32,OutsideDomain=401,RasterZero=4029,WaterMaterial=1,Solid=3,DensityRejected=23
        -45|-36|pin-ground|DensityRejected
        -44|-36|pin-ground|DensityRejected
        -43|-36|pin-ground|Accepted|reed-a|3f867dff|3ebec9bc|c1fffde9|3e919f99|c1d80061|bd7f2326|3f6d2c36|bc3d6f12|bebdf81e|10|pin-ground:-1:-1:2399:0:0:Front
        -42|-36|pin-ground|Accepted|reed-a|3f94d240|3ea6ab9e|c1fd4c49|3e841afa|c1d632d9|bb7a3e53|3e823a02|3d818305|3f770c8d|10|pin-ground:-1:-1:2400:0:0:Front
        -41|-36|pin-ground|Solid
        -40|-36|pin-ground|Accepted|reed-b|3f8740ca|3f02990a|c1ed1bb4|3f110c58|c1d530d9|3c99d129|bddaa7ca|3d6eeac3|3f7e0d8c|10|pin-ground:-1:-1:2402:0:0:Front
        -39|-36|pin-ground|Accepted|reed-a|3f783834|3ea5af72|c1e8c94b|3e033a2d|c1d7ff6d|3e31f45d|3f64bea6|bd881912|3ed1382f|10|pin-ground:-1:-1:2402:0:0:Front
        -38|-36|pin-ground|Accepted|reed-a|3f6d2718|3f26e8f6|c1e1925a|bdd2cbfc|c1da5563|bc055832|bf0492b2|bd9d0c93|3f5a1b66|10|pin-ground:-1:-1:2339:1:0:Front
        -37|-36|pin-ground|Accepted|reed-a|3f70d6d6|3f0c2be7|c1dc6a86|3db0f63b|c1d9d82b|bb877734|3f7e928a|3ce6c204|bdcff6b4|10|pin-ground:-1:-1:2340:1:0:Front
        -36|-36|pin-ground|Accepted|reed-a|3f6af1c8|3e9a08c0|c1d6ddd5|3db51811|c1daab16|bd5ad5c2|3f182d82|3d1a5f83|3f4d2d6b|10|pin-ground:-1:-1:2341:1:0:Front
        -45|-35|pin-ground|DensityRejected
        -44|-35|pin-ground|DensityRejected
        -43|-35|pin-ground|DensityRejected
        -42|-35|pin-ground|Accepted|reed-b|3f81336e|3edb5fa0|c1fb1ee9|3e779800|c1d1c009|3c5b024b|bc787b7a|3d7da743|3f7f74cc|10|pin-ground:-1:-1:2400:1:0:Front
        -41|-35|pin-ground|Solid
        -40|-35|pin-ground|Solid
        -39|-35|pin-ground|DensityRejected
        -38|-35|pin-ground|DensityRejected
        -37|-35|pin-ground|Accepted|reed-b|3f9c852e|3f2d602a|c1de4c4e|bca96e6a|c1d19655|bd69f617|bf2b123f|bda69d82|3f3cbd9c|10|pin-ground:-1:-1:2404:1:0:Front
        -36|-35|pin-ground|DensityRejected
        -46|-34|pin-ground|Accepted|reed-a|3f7841ed|3dda80e0|c208942e|3ece48cc|c1cef1ac|bd973186|3f6e3d10|bc77ec18|beb75ef5|10|pin-ground:-1:-1:2461:0:0:Front
        -45|-34|pin-ground|DensityRejected
        -44|-34|pin-ground|Accepted|reed-b|3f91bc5f|3ece769e|c2038844|3e9f2683|c1cca2c8|be340821|beddaefe|bd4d4b83|3f61f6cc|10|pin-ground:-1:-1:2463:1:0:Front
        -43|-34|pin-ground|Accepted|reed-a|3f67d004|3dbc2438|c201f56f|bc7f5c25|c1ced04c|bdba8bf8|3e316e4a|bdf90db6|3f791bfe|10|pin-ground:-1:-1:2463:0:0:Front
        -42|-34|pin-ground|DensityRejected
        -41|-34|pin-ground|Accepted|reed-a|3f84ae9e|3ed96168|c1f4e5e3|3ea0d694|c1ce8277|3d20ad97|bee78edd|3d4bc3fe|3f63bef1|10|pin-ground:-1:-1:2465:0:0:Front
        -40|-34|pin-ground|Accepted|reed-a|3f9ef57a|3f2f820d|c1efcdb4|3ddcac43|c1cdb91e|3d74c832|bf5bb00f|3cac4650|3f026c65|10|pin-ground:-1:-1:2466:0:0:Front
        -39|-34|pin-ground|Accepted|reed-b|3f6fe80a|3ebc55fc|c1e927cd|3eca63da|c1cbf23e|bca21a64|bf436219|bd0761da|3f251e5d|6|pin-ground:-1:-1:2466:1:0:Front
        -38|-34|pin-ground|DensityRejected
        -37|-34|pin-ground|WaterMaterial
        -36|-34|pin-ground|DensityRejected
        -45|-33|pin-ground|DensityRejected
        -44|-33|pin-ground|Accepted|reed-b|3f77b55e|3f4b60e2|c204ea8b|3eeb0101|c1c722de|3c238531|3d962b4b|3d0ccfe6|3f7f2578|10|pin-ground:-1:-1:2526:0:0:Front
        -43|-33|pin-ground|Accepted|reed-b|3f8b3be2|3e8fe462|c200782f|bd9c2ebd|c1c328f8|3d52288f|3f06c1f2|bd71ef40|3f58bcda|10|pin-ground:-1:-1:2527:1:0:Front
        -42|-33|pin-ground|Accepted|reed-a|3f64d841|3d178200|c1fb93e1|3e03e3bc|c1c5eb5f|3d76bbcc|3f7e28f1|3bcc4d19|3dd36a60|10|pin-ground:-1:-1:2528:0:0:Front
        -41|-33|pin-ground|Accepted|reed-a|3f8b2a20|3f3a0d01|c1f4c011|be000ba4|c1c40cb1|3d38fe20|bf0f75ce|3d35fa10|3f5367b8|10|pin-ground:-1:-1:2529:1:0:Front
        -40|-33|pin-ground|DensityRejected
        -39|-33|pin-ground|Accepted|reed-b|3f808d4c|3f297c37|c1ec9a2a|3ee4a1ba|c1c38abb|3c7beac5|bd431f78|3d7bbca7|3f7f31cc|10|pin-ground:-1:-1:2530:1:0:Front
        -38|-33|pin-ground|DensityRejected
        -37|-33|pin-ground|Accepted|reed-a|3f9ff6b3|3eb8e600|c1e0e540|bda844ab|c1c3dab7|3db1d97b|3f7b43c3|be03f4dc|3de50593|10|pin-ground:-1:-1:2531:0:0:Front
        -36|-33|pin-ground|DensityRejected
        -45|-32|pin-ground|DensityRejected
        -44|-32|pin-ground|Accepted|reed-b|3f773ba8|3d745b70|c2055942|3e5d5e2d|c1c048dd|3df41ee4|3f73185e|bcc08659|3e93f6e6|10|pin-ground:-1:-1:2526:1:0:Front
        -43|-32|pin-ground|Accepted|reed-b|3f8a392b|3ef6c3c6|c20058cb|be0aab90|c1bd38c2|3cdbeb43|3e88534a|bd4e54ab|3f76535d|10|pin-ground:-1:-1:2591:0:0:Front
        -42|-32|pin-ground|Accepted|reed-a|3f9b1d14|3dae0050|c1fb4d02|3e76ca84|c1c297e2|3ca3c206|3ea527cb|bca35f20|3f723539|10|pin-ground:-1:-1:2528:1:0:Front
        -41|-32|pin-ground|Accepted|reed-b|3f7dc3d8|3f108d65|c1f7626d|3e907dd3|c1c047f9|3ca42907|bdfdae74|3d762c11|3f7d829d|10|pin-ground:-1:-1:2529:1:0:Front
        -40|-32|pin-ground|Accepted|reed-b|3f66bfe4|3f3d15bc|c1f09b4b|bd99ca97|c1c1422f|3d186990|bed52ed3|3d520513|3f68301b|10|pin-ground:-1:-1:2529:0:0:Front
        -39|-32|pin-ground|DensityRejected
        -38|-32|pin-ground|DensityRejected
        -37|-32|pin-ground|DensityRejected
        -46|-31|pin-ground|Accepted|reed-a|3f790b2b|3ed4b60e|c208fd2d|3d860205|c1bc11c2|3d28080c|bebc3e7d|3d8efbe6|3f6d2a47|10|pin-ground:-1:-1:2589:0:0:Front
        -45|-31|pin-ground|DensityRejected
        -44|-31|pin-ground|Accepted|reed-a|3f6ca2e9|3f79f5c4|c204958e|3f190c4d|c1b8e2ef|bd5f0e3f|3f70f2d2|3d049ad2|3ea9e739|10|pin-ground:-1:-1:2590:1:0:Front
        -43|-31|pin-ground|Accepted|reed-a|3f9df4c9|3f5228a1|c201d70c|3de081a7|c1bc16d8|3dd58a4c|3ef18ac2|be298634|3f5c1845|10|pin-ground:-1:-1:2591:0:0:Front
        -42|-31|pin-ground|DensityRejected
        -41|-31|pin-ground|Accepted|reed-b|3f5deff8|3ed63dfc|c1f52c92|3e2dd074|c1ba84b5|bd7e8a22|3f6bfe8d|bc49e3e0|bec3c053|10|pin-ground:-1:-1:2593:1:0:Front
        -40|-31|pin-ground|Accepted|reed-a|3f95a44a|3e14228c|c1f069df|3ebd6bb0|c1b94d20|3cf2bbcb|be91c49f|3d655d42|3f74de7e|10|pin-ground:-1:-1:2593:0:0:Front
        -38|-31|pin-ground|DensityRejected
        -37|-31|pin-ground|Accepted|reed-a|3f9c61ed|3eb240b4|c1dc4380|3eafae25|c1b9e33a|3e00929b|3f7caa8a|bd6c679e|3da8a167|10|pin-ground:-1:-1:2596:1:0:Front
        pin-legacy-cover|Complete|4489|Accepted=30,OutsideDomain=397,RasterZero=4031,DensityRejected=31
        -45|27|pin-legacy|DensityRejected
        -43|27|pin-legacy|Accepted|reed-a|3f8180b2|3ee54c90|c20174bb|3f09cc76|41a482dd|3d1a3458|bdbea1e3|3c217d33|3f7eb19c|10|pin-legacy:-1:-1:2783:0:0:Front
        -42|27|pin-legacy|Accepted|reed-b|3f9a4234|3f192355|c1fbf60d|3e1eddf3|41a45c7c|bd4dfc80|bf2b6fc6|be5b8d21|3f359180|10|pin-legacy:-1:-1:2784:0:0:Front
        -37|27|pin-legacy|Accepted|reed-b|3f6d7689|3f5eec0b|c1ddfd88|3e8038cd|41a4662a|bd539c3f|be9a74d4|bdf943a9|3f71b701|10|pin-legacy:-1:-1:2788:0:0:Front
        -45|28|pin-legacy|DensityRejected
        -44|28|pin-legacy|DensityRejected
        -43|28|pin-legacy|DensityRejected
        -42|28|pin-legacy|Accepted|reed-a|3f845a99|3e0481e0|c1fb011a|bea76f55|41aaa4fa|bd2e6800|3f508a98|3ce94abd|3f13e681|10|pin-legacy:-1:-1:2720:1:0:Front
        -41|28|pin-legacy|Accepted|reed-b|3f872082|3ea921aa|c1f3ea25|3e12d634|41a95d49|bba01bff|bdf5121c|3d815c7b|3f7da47d|10|pin-legacy:-1:-1:2721:1:0:Front
        -40|28|pin-legacy|DensityRejected
        -39|28|pin-legacy|Accepted|reed-a|3f6063b8|3edda63a|c1e82312|bbf57a3e|41a74ae1|bd64a98f|3f747221|bcf55e46|be94952c|10|pin-legacy:-1:-1:2786:0:0:Front
        -38|28|pin-legacy|Accepted|reed-b|3f93db0a|3e9700dc|c1e56987|3e9fd98d|41aa0771|3d21448a|bf42f25c|3d4b4c9b|3f2521ec|10|pin-legacy:-1:-1:2723:1:0:Front
        -37|28|pin-legacy|DensityRejected
        -36|28|pin-legacy|DensityRejected
        -45|29|pin-legacy|DensityRejected
        -44|29|pin-legacy|DensityRejected
        -43|29|pin-legacy|Accepted|reed-b|3f8cb116|3f579058|c2025248|bdd126a1|41af3a52|bd9557f1|bf2a0967|bdc90796|3f3ccb88|10|pin-legacy:-1:-1:2719:0:0:Front
        -42|29|pin-legacy|DensityRejected
        -41|29|pin-legacy|Accepted|reed-a|3f9b9d3b|3f369895|c1f34bde|3df00f4f|41ae9be4|3c078402|bea4c474|3d80a31d|3f71d6ac|10|pin-legacy:-1:-1:2721:0:0:Front
        -40|29|pin-legacy|Accepted|reed-b|3f726a17|3f108209|c1eee903|bdf0d695|41ae6f8d|3cf024cc|bf2056d8|3d660b79|3f46e862|10|pin-legacy:-1:-1:2722:1:0:Front
        -39|29|pin-legacy|Accepted|reed-a|3f8b6528|3f77d4cc|c1eaf38f|3e071835|41ae1676|bd818209|3f7d1b25|bb7c4a19|3e0b2845|10|pin-legacy:-1:-1:2722:0:0:Front
        -38|29|pin-legacy|DensityRejected
        -37|29|pin-legacy|DensityRejected
        -36|29|pin-legacy|Accepted|reed-a|3f8d38e9|3e92951a|c1d68c1a|be4b6e95|41ac64a1|3c6df74b|3f68b175|bbe8456f|bed5494a|10|pin-legacy:-1:-1:2725:1:0:Front
        -45|30|pin-legacy|DensityRejected
        -44|30|pin-legacy|DensityRejected
        -43|30|pin-legacy|DensityRejected
        -42|30|pin-legacy|DensityRejected
        -41|30|pin-legacy|Accepted|reed-a|3f8fa8f9|3f66fc77|c1f43d01|bdf65fbd|41b2d59c|bd08dfb1|3eb325d1|3d5c763e|3f6f44c7|10|pin-legacy:-1:-1:2657:1:0:Front
        -40|30|pin-legacy|Accepted|reed-b|3f9360ff|3f130a1a|c1f2aeac|bd6029c8|41b49e4f|bd7b391c|3f65fcda|3c81ff5a|3ede84bc|10|pin-legacy:-1:-1:2657:0:0:Front
        -39|30|pin-legacy|DensityRejected
        -38|30|pin-legacy|DensityRejected
        -37|30|pin-legacy|Accepted|reed-a|3f8451b8|3e80f312|c1db325a|3c28cfdc|41b2a052|3de8eb9c|3f5e167e|bd9cc49d|3ef4cbd4|10|pin-legacy:-1:-1:2660:1:0:Front
        -36|30|pin-legacy|DensityRejected
        -45|31|pin-legacy|DensityRejected
        -44|31|pin-legacy|Accepted|reed-b|3f99dd39|3de5fb18|c20354ba|3f085aac|41b7a401|bc5f02aa|3ecd508b|b8905920|3f6a7d52|10|pin-legacy:-1:-1:2655:0:0:Front
        -43|31|pin-legacy|DensityRejected
        -42|31|pin-legacy|Accepted|reed-a|3f941c1d|3f3e4611|c1fc7451|be9c55b2|41bcad77|bd18b32c|3ed5d19d|3d51cf95|3f680aac|10|pin-legacy:-1:-1:2592:0:0:Front
        -41|31|pin-legacy|Accepted|reed-b|3f83e710|3d7fb560|c1f763fd|be54decb|41b811bf|bd514584|3f67a9de|bd197030|bed773e2|10|pin-legacy:-1:-1:2593:1:0:Front
        -40|31|pin-legacy|Accepted|reed-b|3f9409e9|3f6ff090|c1f04476|3e800cfa|41baecd8|bd6843a2|3f49e3ff|3ce770d9|3f1c9117|10|pin-legacy:-1:-1:2593:0:0:Front
        -39|31|pin-legacy|DensityRejected
        -38|31|pin-legacy|DensityRejected
        -37|31|pin-legacy|DensityRejected
        -36|31|pin-legacy|Accepted|reed-b|3f5daf02|3d804928|c1d8de99|3e3b144b|41b972c4|3e3cb345|3f6f16eb|bd3b4e4d|3e9b0b49|10|pin-legacy:-1:-1:2596:0:0:Front
        -45|32|pin-legacy|DensityRejected
        -44|32|pin-legacy|Accepted|reed-b|3f770b10|3ef458ce|c203a860|3e16f8ec|41be838b|3cdcb9de|3e514714|be3c11be|3f760cbc|10|pin-legacy:-1:-1:2591:1:0:Front
        -43|32|pin-legacy|Accepted|reed-b|3f91286a|3f04fa3b|c202131f|bb839b9a|41be20da|be00aa00|bf51ea9f|bd8fecbf|3f0dd1c6|10|pin-legacy:-1:-1:2591:0:0:Front
        -42|32|pin-legacy|Accepted|reed-b|3f6e8d94|3f0df73c|c1fc24f4|3e0fc49e|41c23d55|3c8f4096|3f600aa9|bb97285c|bef78c1a|10|pin-legacy:-1:-1:2528:1:0:Front
        -41|32|pin-legacy|Accepted|reed-b|3f78c10b|3f37952f|c1f38d01|bb95563e|41c150cf|3d4229b0|bf5cb11c|3d2c28aa|3f00b792|10|pin-legacy:-1:-1:2529:1:0:Front
        -40|32|pin-legacy|DensityRejected
        -39|32|pin-legacy|DensityRejected
        -38|32|pin-legacy|Accepted|reed-a|3f5de485|3f6ba12f|c1e508ee|3e7a6d54|41c27dfc|3e0aada7|3f0a18d5|bd6bdda5|3f543d81|10|pin-legacy:-1:-1:2531:1:0:Front
        -37|32|pin-legacy|DensityRejected
        -36|32|pin-legacy|DensityRejected
        -45|33|pin-legacy|DensityRejected
        -44|33|pin-legacy|Accepted|reed-b|3f968dd4|3eefe650|c20386f7|3ed89574|41c53a9f|bd051349|bea21b1d|bd6d6c4f|3f723bc4|10|pin-legacy:-1:-1:2527:0:0:Front
        -43|33|pin-legacy|Accepted|reed-a|3f715d88|3f70a281|c200eec1|3e4bd256|41c624c5|3e46d673|3f6d9fee|bd9d18e6|3e9da7e6|10|pin-legacy:-1:-1:2527:1:0:Front
        -42|33|pin-legacy|Accepted|reed-b|3f9762d0|3e5c48e8|c1fcd250|3e3221a5|41c376c8|bd3481e3|bf1d417b|bd147605|3f497a48|10|pin-legacy:-1:-1:2528:1:0:Front
        -40|33|pin-legacy|DensityRejected
        -39|33|pin-legacy|Accepted|reed-a|3f81030c|3d7c27f0|c1ec0e05|3eb792e9|41c69c2a|3bde3088|3f2a3b50|bcd81c9a|3f3f1277|10|pin-legacy:-1:-1:2530:0:0:Front
        -38|33|pin-legacy|Accepted|reed-b|3f8d7330|3f6577ea|c1e11581|beaf71ec|41c7d74c|bcb862f8|3f70a59c|b7475bc5|3eae4339|10|pin-legacy:-1:-1:2531:0:0:Front
        pin-deck|Complete|2116|Accepted=13,OutsideDomain=343,RasterZero=1758,DensityRejected=2
        -44|-40|p/deck|OutsideDomain
        -43|-40|p/deck|OutsideDomain
        -41|-40|p/deck|OutsideDomain
        -37|-40|p/deck|OutsideDomain
        -44|-39|p/deck|OutsideDomain
        -43|-39|p/deck|OutsideDomain
        -42|-39|p/deck|OutsideDomain
        -41|-39|p/deck|OutsideDomain
        -40|-39|p/deck|OutsideDomain
        -39|-39|p/deck|OutsideDomain
        -38|-39|p/deck|OutsideDomain
        -37|-39|p/deck|OutsideDomain
        -36|-39|p/deck|OutsideDomain
        -44|-38|p/deck|OutsideDomain
        -43|-38|p/deck|OutsideDomain
        -42|-38|p/deck|OutsideDomain
        -41|-38|p/deck|OutsideDomain
        -40|-38|p/deck|OutsideDomain
        -39|-38|p/deck|OutsideDomain
        -38|-38|p/deck|OutsideDomain
        -37|-38|p/deck|OutsideDomain
        -43|-37|p/deck|OutsideDomain
        -42|-37|p/deck|OutsideDomain
        -41|-37|p/deck|OutsideDomain
        -40|-37|p/deck|Accepted|reed-a|3f87a026|3e8154b4|c1f29db7|40279f50|c1dd425f|bd645a48|becd8a64|bd4a783f|3f69b054|10|p/deck:0:0:65:1:0:Front
        -39|-37|p/deck|Accepted|reed-a|3f779f01|3f745cda|c1e88176|4022cf55|c1dcb447|bdbd296d|bca98ad6|3df67477|3f7cfb55|10|p/deck:0:0:66:1:0:Front
        -38|-37|p/deck|Accepted|reed-a|3f77cd8d|3ecd6640|c1e5640a|402d2a87|c1dc374b|bd9b9c4d|3f7be8ed|bd9763a2|be1279ef|10|p/deck:0:0:66:0:0:Front
        -37|-37|p/deck|OutsideDomain
        -36|-37|p/deck|OutsideDomain
        -44|-36|p/deck|OutsideDomain
        -43|-36|p/deck|OutsideDomain
        -42|-36|p/deck|Accepted|reed-a|3f94d240|3ea6ab9e|c1fd4b5f|4033253e|c1d631b4|bc7b5035|3df130c7|3d9b479f|3f7d722e|10|p/deck:0:0:64:1:0:Front
        -41|-36|p/deck|Accepted|reed-a|3f926f20|3f5e42f3|c1f579b1|40281718|c1d6f24e|bd389e57|bf53f7c1|bd8184a9|3f0e29d0|10|p/deck:0:0:64:0:0:Front
        -40|-36|p/deck|Accepted|reed-b|3f8740ca|3f02990a|c1ed1a6c|401ed32a|c1d52f14|3c1381e4|be9b69c8|3da855ce|3f730052|10|p/deck:0:0:1:1:0:Front
        -39|-36|p/deck|Accepted|reed-a|3f783834|3ea5af72|c1e8b413|4028a583|c1d7fa4f|3d0beaa1|bf0f0cb9|3e35febe|3f4f2fb4|10|p/deck:0:0:2:1:0:Front
        -38|-36|p/deck|Accepted|reed-a|3f6d2718|3f26e8f6|c1e191ac|403bbca1|c1da54f4|bbf88681|bf01d823|bd823017|3f5c0495|10|p/deck:0:0:2:1:0:Front
        -37|-36|p/deck|OutsideDomain
        -44|-35|p/deck|OutsideDomain
        -43|-35|p/deck|OutsideDomain
        -42|-35|p/deck|Accepted|reed-b|3f81336e|3edb5fa0|c1fb2498|403d2cc6|c1d1c354|3d861b37|3f46156a|3d03366c|3f21178a|10|p/deck:0:0:64:0:0:Front
        -41|-35|p/deck|Accepted|reed-a|3f8e14f8|3f1ef55c|c1f52b75|401bc9d7|c1d32508|3d581a9d|bd7b5bcb|bd96eb96|3f7e7642|10|p/deck:0:0:0:1:0:Front
        -40|-35|p/deck|Accepted|reed-a|3f7a4c41|3ea8588e|c1f07cde|401b4412|c1d022b3|bd15ab82|3f73d05d|3d23a906|be999af5|10|p/deck:0:0:0:0:0:Front
        -39|-35|p/deck|DensityRejected
        -38|-35|p/deck|DensityRejected
        -37|-35|p/deck|OutsideDomain
        -44|-34|p/deck|OutsideDomain
        -43|-34|p/deck|OutsideDomain
        -42|-34|p/deck|OutsideDomain
        -41|-34|p/deck|Accepted|reed-a|3f84ae9e|3ed96168|c1f4ebf1|400fc2c4|c1ce87ca|3dd946ad|3ec31dbf|3b893201|3f6b1dc0|10|p/deck:0:0:0:0:0:Front
        -40|-34|p/deck|Accepted|reed-a|3f9ef57a|3f2f820d|c1efccbc|4020a8b6|c1cdbbab|3dc0e618|bf3c23fd|3c4fbf42|3f2be497|10|p/deck:0:0:0:0:0:Front
        -39|-34|p/deck|OutsideDomain
        -38|-34|p/deck|OutsideDomain
        -37|-34|p/deck|OutsideDomain
        -43|-33|p/deck|OutsideDomain
        -42|-33|p/deck|OutsideDomain
        -41|-33|p/deck|OutsideDomain
        -40|-33|p/deck|OutsideDomain
        -39|-33|p/deck|OutsideDomain
        -38|-33|p/deck|OutsideDomain
        -37|-33|p/deck|OutsideDomain
        -36|-33|p/deck|OutsideDomain
        -42|-32|p/deck|OutsideDomain
        -41|-32|p/deck|OutsideDomain
        -40|-32|p/deck|OutsideDomain
        -39|-32|p/deck|OutsideDomain
        -37|-32|p/deck|OutsideDomain
        """;

    [Fact]
    public void PinFixture_MatchesCommittedKeysDecisionsAndPoses()
    {
        string actual = Capture(FoliageFixtures.Pins());
        Assert.Empty(Compare(Pins, actual));
    }

    [Fact]
    public void Pins_AgreeWithIndependentChannelArithmetic()
    {
        Row[] accepted = Rows(Pins).Where(r => r.Reason == nameof(MapFoliageReason.Accepted)).ToArray();
        Assert.NotEmpty(accepted);
        Assert.Contains(accepted, r => r.Owner == FoliageFixtures.PinGround);
        Assert.Contains(accepted, r => r.Owner == "p/deck");
        foreach (Row row in accepted)
        {
            float model = Channel(row.X, row.Z, ModelSalt) * 3.5f;
            Assert.Equal(model < 2f ? "reed-a" : "reed-b", row.Model);
            Assert.Equal(Bits(ScaleMin + Channel(row.X, row.Z, ScaleSalt) * (ScaleMax - ScaleMin)), row.Fields[5]);
            Assert.Equal(Bits(Channel(row.X, row.Z, RankSalt)), row.Fields[6]);
        }
        // Every reason the fixture exercises appears in the pins: subdivided, cut and flagged cells accept, the water
        // overlay and the yawed owner reject, and the edge fade and density decide the rest.
        string[] reasons = Rows(Pins).Select(r => r.Reason).Distinct().Order(StringComparer.Ordinal).ToArray();
        foreach (MapFoliageReason reason in new[] { MapFoliageReason.Accepted, MapFoliageReason.DensityRejected, MapFoliageReason.WaterMaterial,
            MapFoliageReason.Solid, MapFoliageReason.OutsideDomain })
            Assert.Contains(reason.ToString(), reasons);
    }

    [Fact]
    public void SensitivityControls_FailComparison()
    {
        string actual = Capture(FoliageFixtures.Pins());
        Assert.Empty(Compare(Pins, actual));

        // One density byte.
        string density = Capture(FoliageFixtures.Pins((x, z) => x == 2 && z == 1 ? (byte)0 : (byte)(40 + (53 * x + 29 * z) % 216)));
        Assert.NotEmpty(Compare(Pins, density));

        string[] lines = Lines(Pins);
        int rejected = Array.FindIndex(lines, l => l.Contains("|DensityRejected", StringComparison.Ordinal));
        int first = Array.FindIndex(lines, l => l.Contains("|Accepted|", StringComparison.Ordinal));
        Assert.True(rejected >= 0 && first >= 0);

        // One decision.
        Assert.NotEmpty(Compare(Replace(lines, rejected, l => l.Replace("|DensityRejected", "|Solid", StringComparison.Ordinal)), actual));

        // One model choice.
        Assert.NotEmpty(Compare(Replace(lines, first, l => l.Contains("|reed-a|", StringComparison.Ordinal)
            ? l.Replace("|reed-a|", "|reed-b|", StringComparison.Ordinal) : l.Replace("|reed-b|", "|reed-a|", StringComparison.Ordinal)), actual));

        // One orientation beyond its tolerance, and one inside it.
        Assert.NotEmpty(Compare(Replace(lines, first, l => Turned(l, 0.000002)), actual));
        Assert.Empty(Compare(Replace(lines, first, l => Turned(l, 0.0000002)), actual));

        // One position beyond its tolerance, and one inside it.
        Assert.NotEmpty(Compare(Replace(lines, first, l => Moved(l, 0.00002f)), actual));
        Assert.Empty(Compare(Replace(lines, first, l => Moved(l, 0.000002f)), actual));
    }

    /// <summary>The pin text of every layer's chunk (0, 0) of <paramref name="fixture"/>.</summary>
    internal static string Capture(FoliageFixture fixture)
    {
        MapFoliageGenerationContext context = FoliageFixtures.Context(fixture);
        var lines = new List<string>();
        foreach (MapFoliageLayer layer in context.Foliage!.Layers)
        {
            var rows = new List<MapFoliageDecision>();
            MapFoliageSampleBatch batch = MapFoliageDistribution.Generate(context, layer.Id, new(0, 0), null, rows.Add);
            lines.Add(string.Join("|", layer.Id, batch.Status, rows.Count.ToString(CultureInfo.InvariantCulture),
                string.Join(",", rows.GroupBy(r => r.Reason).OrderBy(g => g.Key).Select(g => g.Key + "=" + g.Count().ToString(CultureInfo.InvariantCulture)))));
            foreach (MapFoliageDecision row in rows.Where(r => r.Distribution.Kind != GroundCoverDecisionKind.OutsideArea && r.Reason != MapFoliageReason.RasterZero))
            {
                string key = string.Join("|", row.Address.X.ToString(CultureInfo.InvariantCulture), row.Address.Z.ToString(CultureInfo.InvariantCulture),
                    row.Address.SupportOwnerId, row.Reason);
                if (row.Sample is not { } s) { lines.Add(key); continue; }
                lines.Add(string.Join("|", key, s.AssetId, Bits(s.Scale), Bits(s.ThinningRank), Bits(s.Position.X), Bits(s.Position.Y), Bits(s.Position.Z),
                    Bits(s.Orientation.X), Bits(s.Orientation.Y), Bits(s.Orientation.Z), Bits(s.Orientation.W),
                    s.VisibleMaterial.ToString(CultureInfo.InvariantCulture), Face(s.Face)));
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>Every difference between two pin texts under the gate tolerances.</summary>
    internal static List<string> Compare(string expected, string actual)
    {
        string[] want = Lines(expected), got = Lines(actual);
        var errors = new List<string>();
        if (want.Length != got.Length) errors.Add($"line count {want.Length} != {got.Length}");
        for (int i = 0; i < Math.Min(want.Length, got.Length); i++)
        {
            string[] a = want[i].Split('|'), b = got[i].Split('|');
            if (a.Length != b.Length || a.Length < 15)
            {
                if (want[i] != got[i]) errors.Add($"line {i}: {want[i]} != {got[i]}");
                continue;
            }
            // Keys, reason, model, scale and rank bits, visible material and face are exact.
            foreach (int field in new[] { 0, 1, 2, 3, 4, 5, 6, 14, 15 })
                if (a[field] != b[field]) errors.Add($"line {i} field {field}: {a[field]} != {b[field]}");
            for (int field = 7; field <= 9; field++)
                if (Math.Abs(Float(a[field]) - Float(b[field])) > PositionTolerance) errors.Add($"line {i} position {field}: {Float(a[field]):R} != {Float(b[field]):R}");
            double angle = Angle(Quaternion(a), Quaternion(b));
            if (angle > OrientationTolerance) errors.Add($"line {i} orientation differs by {angle:R} rad");
        }
        return errors;
    }

    sealed record Row(int X, int Z, string Owner, string Reason, string? Model, string[] Fields);

    static IEnumerable<Row> Rows(string pins) => Lines(pins).Select(l => l.Split('|')).Where(f => f.Length >= 4 && int.TryParse(f[0], out _))
        .Select(f => new Row(int.Parse(f[0], CultureInfo.InvariantCulture), int.Parse(f[1], CultureInfo.InvariantCulture), f[2], f[3], f.Length > 4 ? f[4] : null, f));

    static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static string Replace(string[] lines, int index, Func<string, string> change) =>
        string.Join("\n", lines.Select((l, i) => i == index ? change(l) : l));

    static string Turned(string line, double radians)
    {
        string[] f = line.Split('|');
        double[] q = Quaternion(f);
        double c = Math.Cos(radians / 2), s = Math.Sin(radians / 2);
        // q * (0, s, 0, c), a turn about the local up axis.
        double x = q[0] * c - q[2] * s, y = q[1] * c + q[3] * s, z = q[2] * c + q[0] * s, w = q[3] * c - q[1] * s;
        f[10] = Bits((float)x); f[11] = Bits((float)y); f[12] = Bits((float)z); f[13] = Bits((float)w);
        return string.Join("|", f);
    }

    static string Moved(string line, float metres)
    {
        string[] f = line.Split('|');
        f[7] = Bits(Float(f[7]) + metres);
        return string.Join("|", f);
    }

    static double[] Quaternion(string[] f) => [Float(f[10]), Float(f[11]), Float(f[12]), Float(f[13])];

    // The rotation angle between two unit quaternions, in double. After aligning signs, atan2(|a - b|, |a + b|) is half
    // their 4D angle, which is itself half the rotation, so the rotation is four times it.
    static double Angle(double[] a, double[] b)
    {
        double dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
        double sign = dot < 0 ? -1 : 1, minus = 0, plus = 0;
        for (int i = 0; i < 4; i++)
        {
            minus += (a[i] - sign * b[i]) * (a[i] - sign * b[i]);
            plus += (a[i] + sign * b[i]) * (a[i] + sign * b[i]);
        }
        return 4 * Math.Atan2(Math.Sqrt(minus), Math.Sqrt(plus));
    }

    static float Channel(int x, int z, ulong salt)
    {
        unchecked
        {
            ulong key = ((ulong)(uint)x << 32) | (uint)z;
            ulong value = key ^ ((ulong)(uint)Seed * 0x9E3779B97F4A7C15UL) ^ salt;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return (value >> 40) * (1f / 16_777_216f);
        }
    }

    static string Face(KhaozEngine.MapDoc.Surfaces.MapFaceKey? face) => face is not { } f ? "-" : string.Create(CultureInfo.InvariantCulture,
        $"{f.OwnerId}:{f.Patch?.SlotX}:{f.Patch?.SlotZ}:{f.Primitive}:{f.ParentTriangle}:{f.Child}:{f.Side}");

    static string Bits(float value) => BitConverter.SingleToInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture);

    static float Float(string bits) => BitConverter.Int32BitsToSingle(int.Parse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}
