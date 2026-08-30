using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Particles;
using KhaozEngine.Showcase;
using Xunit;

namespace KhaozEngine.Tests.Showcase
{
    /// <summary>
    /// The showcase's Particles and VFX room is the runnable reference for the preset library, which only holds
    /// while it actually cycles all of it. It stopped: 14.6.0 added <c>VfxPresets.EssenceMotes</c> and nothing
    /// added it to the room's array, so the demo under-represented the catalog for releases with no test to say
    /// so (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/258">#258</see>). This pins the room's
    /// roster against the library BY REFLECTION, the same way <c>VfxPresetsTests</c> discovers it, so the next
    /// preset that lands without a demo entry fails here instead of going unnoticed.
    /// </summary>
    public sealed class RoomVfxRosterTests
    {
        static string[] LibraryPresetNames() => typeof(VfxPresets)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(VfxPreset) && p.GetIndexParameters().Length == 0)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        [Fact]
        public void The_room_cycles_every_authored_preset()
        {
            string[] cycled = RoomVfx.Roster
                .Select(e => e.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(LibraryPresetNames(), cycled);
        }

        [Fact]
        public void The_roster_names_no_preset_twice()
        {
            string[] names = RoomVfx.Roster.Select(e => e.Name).ToArray();
            Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void Every_roster_entry_realizes_the_preset_it_names()
        {
            foreach ((string name, Func<VfxPreset> realize, _) in RoomVfx.Roster)
            {
                PropertyInfo? property = typeof(VfxPresets)
                    .GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                Assert.NotNull(property);

                var byName = (VfxPreset)property!.GetValue(null)!;
                VfxPreset byRoster = realize();
                Assert.Equal(byName.Looks.Count, byRoster.Looks.Count);
            }
        }

        [Fact]
        public void EssenceMotes_is_the_one_entry_the_room_gives_an_attractor()
        {
            // Its whole point is draining toward a moving target, so appending it to the array like the other nine
            // and leaving the attractor unset would demo a static puff rather than the feature it was built for.
            string[] attracted = RoomVfx.Roster.Where(e => e.Attracted).Select(e => e.Name).ToArray();
            Assert.Equal(new[] { "EssenceMotes" }, attracted);
        }
    }
}
