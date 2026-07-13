using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using KhaozEngine.App;
using KhaozEngine.MapEditor;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for <see cref="EditorRecentFiles"/> (decision 7): the dedup / cap-10 / most-recent-first
    /// ordering over the settings seam, and a full temp-dir round trip through a real <see cref="FileSettingsStorage"/>
    /// so a fresh store instance re-reads what a prior one persisted.</summary>
    public class EditorRecentFilesTests
    {
        [Fact]
        public void RecentFiles_TouchDedupsAndCaps10_MostRecentFirst()
        {
            var store = new EditorRecentFiles(new InMemorySettingsStorage());

            // Touch 12 distinct paths, then re-touch an older one: it must jump to the front and NOT duplicate.
            for (int i = 0; i < 12; i++)
                store.Touch($"/maps/map{i}.json");

            Assert.Equal(EditorRecentFiles.MaxPaths, store.Paths.Count);   // capped at 10
            Assert.Equal("/maps/map11.json", store.Paths[0]);              // most-recent first
            Assert.Equal("/maps/map2.json", store.Paths[9]);              // map0/map1 fell off the end
            Assert.DoesNotContain("/maps/map0.json", store.Paths);
            Assert.DoesNotContain("/maps/map1.json", store.Paths);

            store.Touch("/maps/map5.json");   // re-touch an entry already in the list
            Assert.Equal("/maps/map5.json", store.Paths[0]);   // moved to front
            Assert.Equal(EditorRecentFiles.MaxPaths, store.Paths.Count);   // no growth: dedup, not append
            int occurrences = 0;
            foreach (string p in store.Paths)
                if (p == "/maps/map5.json") occurrences++;
            Assert.Equal(1, occurrences);   // deduped, not duplicated

            store.Remove("/maps/map5.json");
            Assert.DoesNotContain("/maps/map5.json", store.Paths);
            Assert.Equal(9, store.Paths.Count);
        }

        [Fact]
        public void RecentFiles_PersistsThroughStore()
        {
            AppDataPaths paths = TempPaths(out string root);
            var queue = new PersistenceQueue();
            try
            {
                // Write through one instance over a real file-backed storage, drain the coalesced write queue, then
                // read through a FRESH instance over a fresh storage on the same temp root: the order must survive.
                var storageA = new FileSettingsStorage(paths, queue);
                var writer = new EditorRecentFiles(storageA);
                writer.Touch("/world/alpha.map.json");
                writer.Touch("/world/beta.map.json");
                writer.Touch("/world/gamma.map.json");   // front

                queue.Flush();   // the settings write is queued+coalesced, so flush before re-reading the file

                var storageB = new FileSettingsStorage(paths, queue);
                var reader = new EditorRecentFiles(storageB);

                Assert.Equal(new[] { "/world/gamma.map.json", "/world/beta.map.json", "/world/alpha.map.json" },
                    reader.Paths);

                // The recents ride their own file, not the game's settings.json (no collision, decision 7).
                Assert.True(File.Exists(paths.GetFilePath(EditorRecentFiles.FileName)));
                Assert.False(File.Exists(paths.GetFilePath("settings.json")));
            }
            finally
            {
                queue.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        static AppDataPaths TempPaths(out string root)
        {
            root = Path.Combine(Path.GetTempPath(), "ke-recent-" + Path.GetRandomFileName());
            var env = new FakeAppDataEnvironment { IsMacOS = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;
            return new AppDataPaths("APKiwi", "RecentFilesTest", env);
        }
    }

    /// <summary>An in-memory <see cref="ISettingsStorage"/> that round-trips each save through JSON keyed by the
    /// settings file name, so two stores over ONE instance share persisted state without touching disk. Shared by the
    /// recent-files and landing-scene tests in this namespace.</summary>
    internal sealed class InMemorySettingsStorage : ISettingsStorage
    {
        readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public string SettingsFileName { get; set; } = "settings.json";

        public void SaveSettings<T>(T settings) where T : new()
            => _files[SettingsFileName] = JsonSerializer.Serialize(settings);

        public T LoadSettings<T>() where T : new()
            => _files.TryGetValue(SettingsFileName, out string? json)
                ? JsonSerializer.Deserialize<T>(json) ?? new T()
                : new T();

        public bool SettingsExist() => _files.ContainsKey(SettingsFileName);
    }
}
