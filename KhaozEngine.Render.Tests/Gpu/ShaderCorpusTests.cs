using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE COVERAGE HALF OF <see cref="ShaderCorpus"/>, and the writer that produced the committed tables. The
    /// reasoning for why the corpus exists at all, and why the comparison it serves cannot be a test, is in
    /// <see cref="ShaderCorpus"/>'s own header: the two shader toolchains corrupt each other in one process, so
    /// the only instrument available is two runs of this writer in two processes with a diff between them.
    /// <para>
    /// AN ORDINARY RUN COMPILES NOTHING. It asserts that the committed table's key set is exactly what the
    /// shipped catalog produces, which is the property that would otherwise rot silently: a renderer gaining a
    /// program leaves the corpus describing a set the engine no longer ships, and a migration instrument nobody
    /// noticed was stale is worse than none. The hashes themselves are asserted by the three per-target
    /// byte-equality tests, not here.
    /// </para>
    /// <para>
    /// TO REBUILD THE TABLE: <c>KE_WRITE_SHADER_CORPUS=1 dotnet test KhaozEngine.Render.Tests --filter
    /// ShaderCorpus</c>. Add <c>KE_SHADER_CORPUS_DUMP=&lt;dir&gt;</c> to also drop every emitted artefact as a
    /// file under that directory, which is what makes an old-versus-new comparison readable rather than a set of
    /// moved hashes. The dump is deliberately NOT committed: it is 200-odd emissions and its value is in a diff
    /// taken once, during a swap.
    /// </para>
    /// </summary>
    public sealed partial class ShaderCorpusTests
    {
        const string WriteEnvVar = "KE_WRITE_SHADER_CORPUS";
        const string DumpEnvVar = "KE_SHADER_CORPUS_DUMP";

        [Fact]
        public void TheCommittedCorpus_HasARowForEveryShippedProgramAndNothingElse()
        {
            if (IsWriting())
            {
                WriteTable(ShaderCorpus.Emit(Dumper()));
                return;
            }

            Dictionary<string, string> table = ReadTable(TablePath());
            Assert.True(table.Count > 0,
                $"The shader corpus at {TablePath()} is missing or empty. It is the only record of what the "
                + "outgoing and incoming shader toolchains each emitted for the shipped set, so an absent table "
                + $"is a lost measurement rather than a passing test. Rebuild it with {WriteEnvVar}=1.");

            var expected = new HashSet<string>(ShaderCorpus.ExpectedKeys(), StringComparer.Ordinal);
            var problems = new List<string>();
            foreach (string key in expected.Where(k => !table.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
                problems.Add($"  {key}: shipped, but the corpus has no row for it.");
            foreach (string key in table.Keys.Where(k => !expected.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
                problems.Add($"  {key}: in the corpus, but no longer shipped.");

            Assert.True(problems.Count == 0,
                "The committed shader corpus no longer describes the shipped shader set. A program was added to "
                + $"or removed from {nameof(ShippedShaderPrograms)} without rebuilding it. Rebuild with "
                + $"{WriteEnvVar}=1 and commit, reading the diff first.\n"
                + string.Join("\n", problems));
        }

        /// <summary>
        /// The historical table, the one taken under <c>Veldrid.SPIRV</c> before the swap, is asserted to still
        /// be there and to describe the preserved post-swap key set. It is the OTHER half of the comparison and there is no way
        /// to reproduce it: the toolchain that emitted it is gone from the tree, so a deleted file is a
        /// measurement that cannot be retaken.
        /// </summary>
        [Fact]
        public void TheHistoricalVeldridCorpus_IsStillPresentAndComparable()
        {
            Dictionary<string, string> historical = ReadTable(HistoricalTablePath());
            Assert.True(historical.Count > 0,
                $"The pre-swap corpus at {HistoricalTablePath()} is missing. It records what Veldrid.SPIRV "
                + "1.0.15 emitted for the shipped set on 2026-08-23, and the toolchain that produced it is no "
                + "longer referenced anywhere in the tree, so it cannot be regenerated. Restore it from git.");

            Dictionary<string, string> current = ReadTable(MigrationTablePath());
            IEnumerable<string> drift = historical.Keys.Except(current.Keys, StringComparer.Ordinal)
                .Concat(current.Keys.Except(historical.Keys, StringComparer.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal);
            Assert.True(!drift.Any(),
                "The preserved pre-swap and post-swap measurements describe different shader sets. "
                + "Restore the historical comparison from git. Keys present in one only:\n  "
                + string.Join("\n  ", drift));
        }

        /// <summary>
        /// THE ROW-8 RESULT THAT WOULD OTHERWISE BE A SENTENCE IN A CHANGELOG. Every layout row's hash moved
        /// across the swap, and read alone that says a reflected layout may have PERMUTED, which is exactly the
        /// fault risk R5 exists for and the one a backend cannot survive. It did not: strip the reflected NAMES
        /// out of both tables and all 86 layout rows are identical, so every set, binding, kind, stage and
        /// vertex format survived the swap in place.
        /// <para>
        /// THE NAMES ARE THE WHOLE DIFFERENCE, and losing them is not a regression. The outgoing toolchain
        /// reported SPIRV-Cross's FALLBACK name for a resource the module does not name, which is the literal
        /// SPIR-V id rendered as <c>_25</c>. Those ids are not stable across a compiler version: 29 of the 78
        /// modules moved their id bound in this very swap. This engine binds by id join, never by name (the
        /// name-join spike is deleted, and #586 measured that no join on them is possible), so the outgoing
        /// names were decorative identifiers that would have rotted on the next glslang bump. An empty name is
        /// the honest rendering of a module that carries no <c>OpName</c>.
        /// </para>
        /// </summary>
        [Fact]
        public void TheLayoutsReflectedByBothToolchains_HaveTheSameShapeOnceNamesAreStripped()
        {
            Dictionary<string, string> historical = ReadTable(HistoricalTablePath());
            Dictionary<string, string> current = ReadTable(MigrationTablePath());
            var moved = new List<string>();
            foreach (string key in current.Keys.Where(k => k.Contains(".layout.", StringComparison.Ordinal))
                         .OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!historical.TryGetValue(key, out string? was)) continue;
                if (Shape(was) != Shape(current[key])) moved.Add($"{key}\n    was {Shape(was)}\n    now {Shape(current[key])}");
            }

            Assert.True(moved.Count == 0,
                "A reflected layout changed SHAPE across the toolchain swap, not merely the names it reports. "
                + "That is a permuted or re-kinded binding and the backends index these positionally:\n  "
                + string.Join("\n  ", moved));
        }

        // A layout row rendered without its hash, its size, or the reflected names: what is left is the set,
        // binding, kind, stage and vertex-format structure and nothing else. The stored row is
        // "<hash> <size> <detail>", so the first two fields go before every "name:" inside a bracket becomes ":".
        static string Shape(string row)
        {
            string[] fields = row.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            return fields.Length < 3 ? row : LayoutNames().Replace(fields[2], "$1:");
        }

        [GeneratedRegex(@"([\[,])[^\[\],:|]*:")]
        private static partial Regex LayoutNames();

        // ---- the tables ----------------------------------------------------------------------------------

        static string CorpusDir([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(thisFile)!, "shader-corpus");

        static string TablePath() => Path.Combine(CorpusDir(), "corpus.txt");

        // Keep both migration measurements immutable while the live corpus gains new shipped programs.
        static string MigrationTablePath() => Path.Combine(CorpusDir(), "corpus.khaoz-shaders-migration.txt");

        static string HistoricalTablePath() => Path.Combine(CorpusDir(), "corpus.veldrid-spirv.txt");

        static bool IsWriting()
        {
            string? value = Environment.GetEnvironmentVariable(WriteEnvVar);
            return !string.IsNullOrWhiteSpace(value) && value.Trim() is "1" or "true" or "yes" or "on";
        }

        static Action<string, byte[]>? Dumper()
        {
            string? dir = Environment.GetEnvironmentVariable(DumpEnvVar);
            if (string.IsNullOrWhiteSpace(dir)) return null;
            Directory.CreateDirectory(dir);
            return (key, bytes) => File.WriteAllBytes(Path.Combine(dir, key), bytes);
        }

        static Dictionary<string, string> ReadTable(string path)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(path)) return table;

            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                string[] parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) table[parts[0]] = parts[1];
            }
            return table;
        }

        static void WriteTable(IReadOnlyList<ShaderCorpusRow> rows)
        {
            string path = TablePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var text = new StringBuilder();
            text.Append("# THE SHIPPED SHADER CORPUS, as the toolchain in this tree emits it.\n")
                .Append("# One row per artefact: <program>.<stage>.<target> <sha256> <size>, and one\n")
                .Append("# <program>.layout.<target> row per program carrying the reflected shape in full.\n")
                .Append("# Size is bytes for SPIR-V, characters for emitted text, elements for a layout.\n")
                .Append("#\n")
                .Append("# WHY IT EXISTS. Veldrid.SPIRV and Silk.NET.Shaderc cannot be loaded into one process\n")
                .Append("# (section 2.3 result 4 of docs/design/VELDRID-REMOVAL-DESIGN-2026-08-22.md), so no\n")
                .Append("# test can compare the outgoing toolchain with the incoming one. This file and its\n")
                .Append("# corpus.veldrid-spirv.txt sibling are that comparison, taken in two processes.\n")
                .Append("#\n")
                .Append("# Rebuild: KE_WRITE_SHADER_CORPUS=1 dotnet test KhaozEngine.Render.Tests --filter ShaderCorpus\n")
                .Append("# Rows: ")
                .Append(rows.Count.ToString(CultureInfo.InvariantCulture))
                .Append('\n');

            foreach (ShaderCorpusRow row in rows)
            {
                text.Append(row.Key).Append(' ').Append(row.Hash).Append(' ')
                    .Append(row.Size.ToString(CultureInfo.InvariantCulture));
                if (row.Detail.Length > 0) text.Append(' ').Append(row.Detail);
                text.Append('\n');
            }

            File.WriteAllText(path, text.ToString());
        }
    }
}
