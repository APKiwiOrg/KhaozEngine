using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Gpu;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// WHAT A CACHE HIT HANDS BACK, AND ITS ON-DISK FORM: every stage's emitted MSL, every stage's entry-point
    /// NAME, the binding TABLE read off that emission, and a compute kernel's workgroup size. One entry is one
    /// whole program, because the emission is (a vertex and fragment pair is cross-compiled together and its
    /// indices are assigned across the pair at once).
    ///
    /// <para>
    /// CARRYING THE TABLE IS A REQUIREMENT RATHER THAN A CONVENIENCE (2.2b, pin 6). A hit skips the emission and
    /// the table is READ OUT of the emission, so a payload holding only MSL would have to re-parse it, or worse
    /// fall back to counting arguments, which is the failure the whole binding ruling exists to remove.
    /// <see cref="MetalMslStage.EntryPointName"/> is in the payload for the same reason (M-S5): it is read out of
    /// the emitted text and there is no other source for it.
    /// </para>
    /// <para>
    /// AND THE WORKGROUP SIZE IS IN THE PAYLOAD, WHICH IS WHERE THIS DIVERGES FROM THE DIRECT3D 11 SIBLING ON
    /// PURPOSE. <c>D3D11ShaderBuild.Compute</c> runs the SPIR-V front end even on a cache hit, because the size
    /// lives in the module and its payload is a bare DXBC blob with no container to put three numbers in. This
    /// payload is a written structure with a header and a version already, so carrying them costs twelve bytes
    /// and buys the OTHER half of the compile: on this backend a compute hit skips glslang as well as SPIRV-Cross.
    /// They cannot drift from the shader, because the key they are stored under is a hash of that shader's own
    /// source (<see cref="MetalShaderKey"/>), so a changed source is a different entry rather than a stale one.
    /// </para>
    /// <para>
    /// THE FORMAT IS ENGINE-AUTHORED AND AUTHENTICATED, which the two sibling caches are not and do not need to
    /// be. A mangled DXBC fails inside <c>CreateVertexShader</c> and a mangled <c>VkPipelineCache</c> blob fails
    /// the driver's own header check, so both have a downstream reader that refuses them. A mangled MSL payload
    /// has no such reader: the text might still compile, and a mangled TABLE has nothing at all below it, so it
    /// would bind the wrong resource and render a wrong pixel with no error anywhere. That is the exact class
    /// section 2.2b exists to close, so the file carries a SHA-256 of its own body and any mismatch is corruption.
    /// </para>
    /// <para>
    /// EVERY FAILURE IS A MISS AND THE FILE IS DELETED, never an exception and never a partial answer. A parse
    /// that runs out of bytes, a header from another engine version or another format, a hash that does not
    /// match, a key that does not match the file it was read from, or a table the structural checks refuse: all
    /// of them answer null, and <see cref="MetalMslCache"/> deletes the entry so the next launch does not read it
    /// again.
    /// </para>
    /// </summary>
    internal sealed class MetalMslCacheEntry
    {
        /// <summary>The file's first bytes, so a file that is not one of these is rejected before anything else
        /// is read. ASCII, and short enough to eyeball in a hex dump.</summary>
        internal static readonly byte[] Magic = "KEMSL"u8.ToArray();

        /// <summary>The payload FORMAT's own version, bumped by hand when the fields below change. An entry
        /// written by an older format is a miss rather than a misread: the engine version in the key already
        /// separates releases, and this separates two formats within one release, which is what a developer
        /// iterating on this file hits.</summary>
        internal const int FormatVersion = 1;

        /// <summary>Bytes of SHA-256 appended after the body.</summary>
        internal const int HashLength = 32;

        readonly MetalMslProgram _program;

        /// <param name="program">The emitted program: every stage's MSL and entry-point name, plus its table.</param>
        /// <param name="x">A compute kernel's workgroup size on X, or 0 for a graphics program.</param>
        /// <param name="y">A compute kernel's workgroup size on Y, or 0 for a graphics program.</param>
        /// <param name="z">A compute kernel's workgroup size on Z, or 0 for a graphics program.</param>
        internal MetalMslCacheEntry(MetalMslProgram program, uint x, uint y, uint z)
        {
            ArgumentNullException.ThrowIfNull(program);

            _program = program;
            ThreadGroupSizeX = x;
            ThreadGroupSizeY = y;
            ThreadGroupSizeZ = z;
        }

        /// <summary>The emitted program this entry carries.</summary>
        internal MetalMslProgram Program => _program;

        /// <summary>A compute kernel's workgroup size on X, 0 for a graphics program.</summary>
        internal uint ThreadGroupSizeX { get; }

        /// <summary>A compute kernel's workgroup size on Y, 0 for a graphics program.</summary>
        internal uint ThreadGroupSizeY { get; }

        /// <summary>A compute kernel's workgroup size on Z, 0 for a graphics program.</summary>
        internal uint ThreadGroupSizeZ { get; }

        /// <summary>
        /// The file this entry is stored as: the magic, the format version, the engine version, the key it is
        /// stored under, every stage, the workgroup size, the reflected layouts, every table entry, and then a
        /// SHA-256 of all of it.
        /// <para>
        /// THE KEY IS WRITTEN INSIDE THE FILE as well as being its name. A file moved, copied or renamed under
        /// another key would otherwise be read as that other program's emission, which is a wrong table arriving
        /// through the file system rather than through a bug.
        /// </para>
        /// </summary>
        internal byte[] Serialize(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            using var body = new MemoryStream(1 << 16);
            using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(MetalShaderKey.EngineVersion);
                writer.Write(key);

                writer.Write(_program.Stages.Count);
                foreach (MetalMslStage stage in _program.Stages)
                {
                    writer.Write((int)stage.Stage);
                    writer.Write(stage.EntryPointName);
                    writer.Write(stage.Msl);
                }

                writer.Write(ThreadGroupSizeX);
                writer.Write(ThreadGroupSizeY);
                writer.Write(ThreadGroupSizeZ);

                WriteLayouts(writer, _program.Table.Layouts);
                WriteEntries(writer, _program.Table);
            }

            byte[] bytes = new byte[body.Length + HashLength];
            body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(bytes);
            SHA256.HashData(bytes.AsSpan(0, (int)body.Length), bytes.AsSpan((int)body.Length));
            return bytes;
        }

        /// <summary>
        /// The entry <paramref name="file"/> holds, or null when it is not one, is not this format, is not this
        /// engine version, is not this key, does not hash, or does not survive the table's structural checks.
        /// Nothing here throws.
        /// </summary>
        /// <param name="file">The file's whole contents.</param>
        /// <param name="key">The key the file was read under, which the payload must restate.</param>
        /// <param name="label">A name for the program, for the exception the table build would raise.</param>
        internal static MetalMslCacheEntry? TryParse(byte[] file, string key, string label)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            if (!Authenticated(file)) return null;

            try
            {
                return Read(file, key, label);
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or FormatException
                or ArgumentException or OverflowException or ShaderValidationException)
            {
                // Every one of these is the same answer: the bytes on disk are not an entry this engine wrote,
                // so there is nothing to trust and nothing to salvage. The caller deletes the file and compiles.
                return null;
            }
        }

        // THE HASH IS CHECKED BEFORE ONE FIELD IS READ, which is what makes every length and count below safe to
        // act on: a truncated or mangled file cannot reach the reader and ask it to allocate an array of a size
        // nobody wrote.
        static bool Authenticated(byte[] file)
        {
            if (file.Length <= HashLength + Magic.Length + sizeof(int)) return false;

            int bodyLength = file.Length - HashLength;
            Span<byte> hash = stackalloc byte[HashLength];
            SHA256.HashData(file.AsSpan(0, bodyLength), hash);
            return hash.SequenceEqual(file.AsSpan(bodyLength));
        }

        static MetalMslCacheEntry? Read(byte[] file, string key, string label)
        {
            using var body = new MemoryStream(file, 0, file.Length - HashLength, writable: false);
            using var reader = new BinaryReader(body, Encoding.UTF8, leaveOpen: true);

            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) return null;
            if (reader.ReadInt32() != FormatVersion) return null;
            if (!string.Equals(reader.ReadString(), MetalShaderKey.EngineVersion, StringComparison.Ordinal))
                return null;
            if (!string.Equals(reader.ReadString(), key, StringComparison.Ordinal)) return null;

            int stageCount = reader.ReadInt32();
            if (stageCount is < 1 or > 2) return null;

            var stages = new MetalMslStage[stageCount];
            var seen = new HashSet<MetalShaderStage>();
            for (int i = 0; i < stageCount; i++)
            {
                if (!TryStage(reader.ReadInt32(), out MetalShaderStage stage) || !seen.Add(stage)) return null;

                string entryPoint = reader.ReadString();
                string msl = reader.ReadString();
                if (entryPoint.Length == 0 || msl.Length == 0) return null;

                stages[i] = new MetalMslStage(stage, entryPoint, msl);
            }

            uint x = reader.ReadUInt32(), y = reader.ReadUInt32(), z = reader.ReadUInt32();
            if (!SizeMatchesShape(seen, x, y, z)) return null;

            GpuResourceLayoutDescription[]? layouts = ReadLayouts(reader);
            if (layouts is null) return null;

            List<KeyValuePair<MetalIndexTableKey, MetalIndexTableEntry>>? entries = ReadEntries(reader);
            if (entries is null) return null;
            if (body.Position != body.Length) return null;   // trailing bytes nothing wrote

            var program = new MetalMslProgram(
                stages, MetalShaderIndexTable.FromCache(entries, layouts, seen, label));
            return new MetalMslCacheEntry(program, x, y, z);
        }

        // THE WORKGROUP SIZE AND THE STAGE SET HAVE TO AGREE, because they are two halves of one fact. A compute
        // program is one compute stage and dispatches at a size no dimension of which can be zero, and a graphics
        // program has no such size at all, so a payload claiming both is not one this engine wrote.
        static bool SizeMatchesShape(HashSet<MetalShaderStage> stages, uint x, uint y, uint z)
            => stages.Contains(MetalShaderStage.Compute)
                ? stages.Count == 1 && x >= 1 && y >= 1 && z >= 1
                : x == 0 && y == 0 && z == 0;

        static void WriteLayouts(BinaryWriter writer, IReadOnlyList<GpuResourceLayoutDescription> layouts)
        {
            writer.Write(layouts.Count);
            foreach (GpuResourceLayoutDescription layout in layouts)
            {
                writer.Write(layout.Elements.Length);
                foreach (GpuResourceLayoutElement element in layout.Elements)
                {
                    // THE NAME AND THE STAGE VISIBILITY RIDE ALONG even though the table's own ContentKey renders
                    // neither, so a rebuilt table is field-for-field what the emission produced rather than only
                    // observably equal to it. A future member that starts reading either off Layouts then finds
                    // the same value on a hit as on a miss, which is the cheap half of what #594 is about.
                    writer.Write(element.Name ?? string.Empty);
                    writer.Write((int)element.Kind);
                    writer.Write((int)element.Stages);
                    writer.Write(element.Dynamic);
                }
            }
        }

        static GpuResourceLayoutDescription[]? ReadLayouts(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0) return null;

            var layouts = new GpuResourceLayoutDescription[count];
            for (int set = 0; set < count; set++)
            {
                int elementCount = reader.ReadInt32();
                if (elementCount < 0) return null;

                var elements = new GpuResourceLayoutElement[elementCount];
                for (int i = 0; i < elementCount; i++)
                {
                    string name = reader.ReadString();
                    int kind = reader.ReadInt32();
                    int stages = reader.ReadInt32();
                    bool dynamic = reader.ReadBoolean();

                    if (!Enum.IsDefined((GpuResourceKind)kind)) return null;
                    elements[i] = new GpuResourceLayoutElement(
                        name, (GpuResourceKind)kind, (GpuShaderStages)stages, dynamic);
                }
                layouts[set] = new GpuResourceLayoutDescription(elements);
            }
            return layouts;
        }

        static void WriteEntries(BinaryWriter writer, MetalShaderIndexTable table)
        {
            writer.Write(table.Count);
            foreach ((MetalIndexTableKey key, MetalIndexTableEntry entry) in table.Entries())
            {
                writer.Write(key.Set);
                writer.Write(key.Binding);
                writer.Write((int)key.Stage);
                writer.Write((int)entry.Space);
                writer.Write(entry.Index);
            }
        }

        static List<KeyValuePair<MetalIndexTableKey, MetalIndexTableEntry>>? ReadEntries(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0) return null;

            var entries = new List<KeyValuePair<MetalIndexTableKey, MetalIndexTableEntry>>(count);
            for (int i = 0; i < count; i++)
            {
                int set = reader.ReadInt32();
                int binding = reader.ReadInt32();
                if (!TryStage(reader.ReadInt32(), out MetalShaderStage stage)) return null;

                int space = reader.ReadInt32();
                int index = reader.ReadInt32();
                if (!Enum.IsDefined((MetalIndexSpace)space)) return null;

                entries.Add(new KeyValuePair<MetalIndexTableKey, MetalIndexTableEntry>(
                    new MetalIndexTableKey(set, binding, stage),
                    new MetalIndexTableEntry((MetalIndexSpace)space, index)));
            }
            return entries;
        }

        static bool TryStage(int value, out MetalShaderStage stage)
        {
            stage = (MetalShaderStage)value;
            return Enum.IsDefined(stage);
        }
    }
}
