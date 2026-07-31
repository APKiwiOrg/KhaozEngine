using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// The known-injector list and the pure matching / wording around it: given the file names of the modules
    /// loaded into this process, which of them are third-party overlays that hook Direct3D. Overlay injectors are
    /// a recurring cause of stalls, corrupted frames, and driver-level crashes that look exactly like an engine
    /// bug, so a session log naming the ones that were actually present turns a week of guessing into one line.
    /// <para>
    /// Everything here is pure and device-free, so it is headless-testable on any OS. The half that can only run
    /// on Windows (enumerating the live process's modules) lives in <c>Internal/InjectedModuleProbe</c> and hands
    /// its findings to <see cref="Match"/>. <c>GpuDeviceContext.InjectedModules</c> carries the result, and
    /// <see cref="Describe"/> renders it for a log line or a game debug overlay.
    /// </para>
    /// </summary>
    public static class GpuInjectedModules
    {
        // The list itself, in the order it is reported. An ARRAY rather than the dictionary below is the source of
        // order, because Dictionary does not contract an enumeration order and KnownModuleNames promises one.
        static readonly (string FileName, string Product)[] Known =
        {
            ("NahimicOSD.dll", "Nahimic audio on-screen display"),
            ("NahimicOSD64.dll", "Nahimic audio on-screen display"),
            ("SS2OSD.dll", "Sonic Studio on-screen display (Nahimic)"),
            ("SS2OSD64.dll", "Sonic Studio on-screen display (Nahimic)"),
            ("RTSSHooks.dll", "RivaTuner Statistics Server, as used by MSI Afterburner"),
            ("RTSSHooks64.dll", "RivaTuner Statistics Server, as used by MSI Afterburner"),
            ("nvspcap.dll", "NVIDIA GeForce Experience overlay"),
            ("nvspcap64.dll", "NVIDIA GeForce Experience overlay"),
            ("DiscordHook.dll", "Discord overlay"),
            ("DiscordHook64.dll", "Discord overlay"),
            ("graphics-hook32.dll", "OBS game-capture hook"),
            ("graphics-hook64.dll", "OBS game-capture hook"),
        };

        // Lookup by module file name, case-insensitive because Windows reports module names in whatever case the
        // loader recorded. The value carries the canonical spelling, so the log line reads the same however the
        // name arrived.
        static readonly Dictionary<string, (string FileName, string Product)> ByFileName = BuildLookup();

        // Both separators, always, on every OS. Path.GetFileName would be the obvious call and is the wrong one:
        // it honours only the HOST separator, so a Windows path handed to it on macOS comes back whole and the
        // match silently misses. That would make this list behave differently in a headless test than in the
        // place it actually runs, which is the one failure a pure matcher exists to rule out.
        static readonly char[] Separators = { '/', '\\' };

        /// <summary>
        /// Every module file name this matcher knows, in canonical spelling and declaration order. Public so a
        /// game can show the list it is being screened against rather than an unexplained verdict.
        /// </summary>
        public static IReadOnlyList<string> KnownModuleNames { get; } = BuildKnownNames();

        /// <summary>What <see cref="Describe"/> reports for null: the scan did not run at all. Off Windows that is
        /// the normal state and not a fault, so it reads as "not checked" rather than "nothing found".</summary>
        public const string UnknownDescription =
            "unknown (the loaded-module scan is Windows-only, or it did not run)";

        /// <summary>What <see cref="Describe"/> reports for an empty match list: the scan ran and found none of
        /// the known injectors. Distinct from <see cref="UnknownDescription"/> on purpose, because "we looked and
        /// it is clean" and "we never looked" are opposite facts to a reader triaging a crash.</summary>
        public const string NoneDescription = "none detected";

        /// <summary>
        /// The known injectors among <paramref name="moduleFileNames"/>, in canonical spelling, in the order they
        /// were first seen, with duplicates removed. Matching is case-insensitive on the file name only, and a
        /// full path is accepted (anything up to the last <c>/</c> or <c>\</c> is dropped). Null and blank entries
        /// are skipped rather than throwing, since the input is whatever the OS reported.
        /// </summary>
        public static IReadOnlyList<string> Match(IEnumerable<string?> moduleFileNames)
        {
            ArgumentNullException.ThrowIfNull(moduleFileNames);

            var hits = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? raw in moduleFileNames)
            {
                string name = FileNameOf(raw);
                if (name.Length == 0) continue;
                if (!ByFileName.TryGetValue(name, out (string FileName, string Product) known)) continue;
                if (seen.Add(known.FileName)) hits.Add(known.FileName);
            }
            return hits;
        }

        /// <summary>
        /// The INFO-line body for a match list: <see cref="UnknownDescription"/> for null,
        /// <see cref="NoneDescription"/> for empty, otherwise each match named with the product behind it. The
        /// product matters more than the DLL: a tester knows whether they run MSI Afterburner, and does not know
        /// what RTSSHooks64.dll is.
        /// </summary>
        public static string Describe(IReadOnlyList<string>? matches)
        {
            if (matches is null) return UnknownDescription;
            if (matches.Count == 0) return NoneDescription;
            return string.Join(", ", Named(matches));
        }

        /// <summary>True only when the scan ran AND matched something. Null never warns, because "we could not
        /// look" is not evidence that anything is hooked.</summary>
        public static bool ShouldWarn(IReadOnlyList<string>? matches) => matches is { Count: > 0 };

        /// <summary>
        /// The WARN body logged when <see cref="ShouldWarn"/> is true. Written for a tester reading their own log
        /// with no graphics background: what was found, why it is worth knowing, and the one thing to try next.
        /// </summary>
        public static string Warning(IReadOnlyList<string> matches)
        {
            ArgumentNullException.ThrowIfNull(matches);
            return $"Third-party overlay or capture software is hooked into this process: {Describe(matches)}. "
                + "Software like this injects itself into Direct3D to draw on top of the game, and it is a known "
                + "cause of stutter, corrupted frames, and crashes deep inside the graphics driver that look "
                + "exactly like a bug in the game. If this session misbehaves, close or disable it and try again "
                + "before investigating anything else.";
        }

        static IEnumerable<string> Named(IReadOnlyList<string> matches)
        {
            foreach (string match in matches)
            {
                yield return ByFileName.TryGetValue(match, out (string FileName, string Product) known)
                    ? $"{known.FileName} ({known.Product})"
                    : match;
            }
        }

        static string FileNameOf(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string trimmed = raw.Trim();
            int cut = trimmed.LastIndexOfAny(Separators);
            return cut < 0 ? trimmed : trimmed[(cut + 1)..];
        }

        static Dictionary<string, (string FileName, string Product)> BuildLookup()
        {
            var map = new Dictionary<string, (string, string)>(Known.Length, StringComparer.OrdinalIgnoreCase);
            foreach ((string FileName, string Product) entry in Known) map[entry.FileName] = entry;
            return map;
        }

        static string[] BuildKnownNames()
        {
            var names = new string[Known.Length];
            for (int i = 0; i < Known.Length; i++) names[i] = Known[i].FileName;
            return names;
        }
    }
}
