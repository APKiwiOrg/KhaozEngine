using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Headless tests for <see cref="GpuInjectedModules"/>, the pure half of the loaded-module injector scan: which
    /// names match, how the answer reads, and when the WARN arm fires.
    /// <para>
    /// The scan itself (<c>Internal/InjectedModuleProbe</c>) is NOT covered here and cannot be. It reads
    /// <c>Process.Modules</c> on Windows, and this suite runs on macOS and Linux where the probe is a guard and a
    /// return. Everything that could be factored out of it was, which is exactly this matching plus the wording.
    /// </para>
    /// </summary>
    public sealed class GpuInjectedModulesTests
    {
        [Fact]
        public void KnownModuleNames_CoversEveryInjectorOnTheList()
        {
            string[] expected =
            {
                "NahimicOSD.dll", "NahimicOSD64.dll", "SS2OSD.dll", "SS2OSD64.dll",
                "RTSSHooks.dll", "RTSSHooks64.dll", "nvspcap.dll", "nvspcap64.dll",
                "DiscordHook.dll", "DiscordHook64.dll", "graphics-hook32.dll", "graphics-hook64.dll",
            };

            Assert.Equal(expected, GpuInjectedModules.KnownModuleNames);
        }

        [Fact]
        public void Match_FindsTheKnownNamesAndIgnoresEverythingElse()
        {
            IReadOnlyList<string> hits = GpuInjectedModules.Match(new[]
            {
                "KhaozEngine.Gpu.dll", "RTSSHooks64.dll", "kernel32.dll", "graphics-hook64.dll",
            });

            Assert.Equal(new[] { "RTSSHooks64.dll", "graphics-hook64.dll" }, hits);
        }

        [Fact]
        public void Match_IsCaseInsensitiveAndReturnsTheCanonicalSpelling()
        {
            // Windows reports whatever case the loader recorded, so the log line must not vary with it.
            IReadOnlyList<string> hits = GpuInjectedModules.Match(new[] { "rtsshooks64.DLL", "NVSPCAP64.dll" });

            Assert.Equal(new[] { "RTSSHooks64.dll", "nvspcap64.dll" }, hits);
        }

        [Fact]
        public void Match_AcceptsAFullPathOnEitherSeparator()
        {
            // Asserted on BOTH separators because this suite runs on macOS and Linux while the probe runs on
            // Windows. Path.GetFileName would pass here on a Unix path and silently miss every Windows one.
            IReadOnlyList<string> hits = GpuInjectedModules.Match(new[]
            {
                @"C:\Program Files\RivaTuner Statistics Server\RTSSHooks64.dll",
                "/opt/obs/graphics-hook64.dll",
            });

            Assert.Equal(new[] { "RTSSHooks64.dll", "graphics-hook64.dll" }, hits);
        }

        [Fact]
        public void Match_SkipsNullAndBlankEntriesInsteadOfThrowing()
        {
            // The input is whatever the OS reported, so a hostile entry must not take down device creation.
            IReadOnlyList<string> hits = GpuInjectedModules.Match(new[] { null, "", "   ", "DiscordHook.dll" });

            Assert.Equal(new[] { "DiscordHook.dll" }, hits);
        }

        [Fact]
        public void Match_DeduplicatesAcrossCasing()
        {
            IReadOnlyList<string> hits = GpuInjectedModules.Match(new[] { "SS2OSD.dll", "ss2osd.dll" });

            Assert.Equal(new[] { "SS2OSD.dll" }, hits);
        }

        [Fact]
        public void Match_CleanProcess_IsAnEmptyListNotNull()
        {
            // Empty and null are different answers downstream, so the matcher must never blur them.
            Assert.Empty(GpuInjectedModules.Match(new[] { "kernel32.dll", "d3d11.dll" }));
        }

        [Fact]
        public void Match_NullInput_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => GpuInjectedModules.Match(null!));
        }

        [Fact]
        public void Describe_Null_IsNotScannedAndEmpty_IsClean()
        {
            // The whole point of the null/empty split: "we never looked" must not read as "we looked and it is
            // clean" to someone triaging a crash.
            Assert.Equal(GpuInjectedModules.UnknownDescription, GpuInjectedModules.Describe(null));
            Assert.Equal(GpuInjectedModules.NoneDescription, GpuInjectedModules.Describe(Array.Empty<string>()));
            Assert.NotEqual(GpuInjectedModules.UnknownDescription, GpuInjectedModules.NoneDescription);
        }

        [Fact]
        public void Describe_NamesTheProductBehindEachModule()
        {
            string text = GpuInjectedModules.Describe(new[] { "RTSSHooks64.dll", "nvspcap64.dll" });

            Assert.Contains("RTSSHooks64.dll", text);
            // A tester knows whether they run MSI Afterburner. They do not know what RTSSHooks64.dll is.
            Assert.Contains("MSI Afterburner", text);
            Assert.Contains("nvspcap64.dll", text);
            Assert.Contains("GeForce Experience", text);
        }

        [Fact]
        public void Describe_EveryKnownModule_CarriesAProductLabel()
        {
            foreach (string module in GpuInjectedModules.KnownModuleNames)
            {
                string text = GpuInjectedModules.Describe(new[] { module });
                Assert.StartsWith(module + " (", text);
                Assert.EndsWith(")", text);
            }
        }

        [Fact]
        public void ShouldWarn_OnlyWhenTheScanRanAndMatchedSomething()
        {
            Assert.True(GpuInjectedModules.ShouldWarn(new[] { "DiscordHook64.dll" }));
            Assert.False(GpuInjectedModules.ShouldWarn(Array.Empty<string>()));

            // "We could not look" is not evidence that anything is hooked. A warning nobody can act on trains the
            // reader to skip the one that matters.
            Assert.False(GpuInjectedModules.ShouldWarn(null));
        }

        [Fact]
        public void Warning_NamesEveryMatchAndSaysWhatToDo()
        {
            string warning = GpuInjectedModules.Warning(new[] { "NahimicOSD64.dll", "graphics-hook32.dll" });

            Assert.Contains("NahimicOSD64.dll", warning);
            Assert.Contains("graphics-hook32.dll", warning);
            Assert.Contains("OBS", warning);
            // It must name the escape hatch, otherwise the reader knows they have a problem and not one thing to
            // try about it.
            Assert.Contains("close or disable", warning);
        }

        [Fact]
        public void KnownModuleNames_HasNoDuplicates()
        {
            Assert.Equal(
                GpuInjectedModules.KnownModuleNames.Count,
                GpuInjectedModules.KnownModuleNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }
}
