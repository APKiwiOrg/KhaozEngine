using System;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The tmp-and-rename write every world file goes through. Pins that the tmp name is this call's alone
/// (#790): a single fixed <c>path + ".tmp"</c> is shared by every writer into the directory, so two saves can
/// truncate each other's tmp and move half-written bytes over the target.</summary>
public class TileWorldFileWriteAtomicTests
{
    static byte[] Filled(byte value, int length)
    {
        var b = new byte[length];
        Array.Fill(b, value);
        return b;
    }

    [Fact]
    public void The_tmp_name_is_not_the_fixed_one()
    {
        using var dir = new TempDir();
        string path = dir.Sub("world.json");
        // Stands in for another writer holding the shared tmp: with one fixed name this write cannot even start,
        // which is the same collision that lets two real writers overwrite each other's tmp.
        Directory.CreateDirectory(path + ".tmp");

        byte[] payload = Filled(0xAB, 4096);
        TileWorldFile.WriteAtomic(path, payload);

        Assert.Equal(payload, File.ReadAllBytes(path));
        Assert.True(Directory.Exists(path + ".tmp"), "the write must not have touched the occupied name");
    }

    [Fact]
    public async Task Two_writers_racing_on_one_path_leave_one_complete_file()
    {
        using var dir = new TempDir();
        string path = dir.Sub("world.json");
        byte[] a = Filled(0xAA, 512 * 1024), b = Filled(0xBB, 512 * 1024);

        for (int round = 0; round < 20; round++)
        {
            Task first = Task.Run(() => TileWorldFile.WriteAtomic(path, a));
            Task second = Task.Run(() => TileWorldFile.WriteAtomic(path, b));
            await Task.WhenAll(first, second);

            byte[] landed = File.ReadAllBytes(path);
            Assert.True(landed.AsSpan().SequenceEqual(a) || landed.AsSpan().SequenceEqual(b),
                $"round {round} landed {landed.Length} bytes that are neither writer's payload");
        }

        // Every tmp this class writes is renamed onto the target, so nothing is left behind.
        Assert.Equal(new[] { path }, Directory.GetFiles(dir.Path));
    }
}
