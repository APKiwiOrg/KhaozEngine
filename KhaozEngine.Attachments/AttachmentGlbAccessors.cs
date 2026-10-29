using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;

namespace KhaozEngine.Attachments;

internal sealed class AttachmentGlbAccessors
{
    readonly JsonElement root;
    readonly ReadOnlyMemory<byte> binary;
    readonly bool refuseCompression;

    internal AttachmentGlbAccessors(JsonElement root, ReadOnlyMemory<byte> binary, bool refuseCompression = false)
    {
        this.root = root;
        this.refuseCompression = refuseCompression;
        int declaredLength = Integer(root.GetProperty("buffers")[0], "byteLength", "buffer 0");
        if (declaredLength > binary.Length)
            throw new InvalidDataException("buffer 0: declared length exceeds the BIN chunk.");
        this.binary = binary[..declaredLength];
    }

    internal Accessor Read(JsonElement reference, string context, string type, bool joints = false, bool weights = false, bool scalarIndices = false)
    {
        int index = Index(reference, context + " accessor");
        context += $" accessor {index}";
        JsonElement value = Entry(root, "accessors", index, context);
        if (!value.TryGetProperty("type", out JsonElement shape) || shape.ValueKind != JsonValueKind.String || shape.GetString() != type)
            throw new InvalidDataException($"{context}: expected {type}.");
        int components = type == "MAT4" ? 16 : type == "VEC3" ? 3 : type == "SCALAR" ? 1 : 4;
        int componentType = Integer(value, "componentType", context);
        bool normalized = false;
        if (value.TryGetProperty("normalized", out JsonElement normalization))
        {
            if (normalization.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"{context}: normalized must be a boolean.");
            normalized = normalization.GetBoolean();
        }
        bool legal = scalarIndices ? (componentType is 5121 or 5123 or 5125) && !normalized
            : joints ? (componentType is 5121 or 5123) && !normalized
            : weights ? (componentType == 5126 && !normalized) || ((componentType is 5121 or 5123) && normalized)
            : componentType == 5126 && !normalized;
        if (!legal)
            throw new InvalidDataException($"{context}: unsupported componentType or normalized encoding.");
        int componentSize = ComponentSize(componentType);
        int elementSize = componentSize * components;
        int count = Integer(value, "count", context);
        if (count == 0)
            throw new InvalidDataException($"{context}: count must be positive.");
        int offset = Integer(value, "byteOffset", context, 0);
        ReadOnlyMemory<byte> data = default;
        int stride = elementSize;
        if (value.TryGetProperty("bufferView", out JsonElement viewReference))
            data = Range(viewReference, offset, count, elementSize, componentSize, context, true, out stride);
        else if (offset != 0)
            throw new InvalidDataException($"{context}: byteOffset requires a bufferView.");

        int[] sparseIndices = System.Array.Empty<int>();
        ReadOnlyMemory<byte> sparseValues = default;
        if (value.TryGetProperty("sparse", out JsonElement sparse))
        {
            string sparseContext = context + " sparse";
            int sparseCount = Integer(sparse, "count", sparseContext);
            if (sparseCount == 0 || sparseCount > count)
                throw new InvalidDataException($"{sparseContext}: count is outside the accessor.");
            JsonElement indices = Object(sparse, "indices", sparseContext);
            string indexContext = sparseContext + " indices";
            int indexType = Integer(indices, "componentType", indexContext);
            if (indexType is not (5121 or 5123 or 5125))
                throw new InvalidDataException($"{indexContext}: componentType must be unsigned byte, short or int.");
            int indexSize = ComponentSize(indexType);
            ReadOnlyMemory<byte> indexData = Range(Required(indices, "bufferView", indexContext),
                Integer(indices, "byteOffset", indexContext, 0), sparseCount, indexSize, indexSize, indexContext, false, out _);
            JsonElement values = Object(sparse, "values", sparseContext);
            string valueContext = sparseContext + " values";
            sparseValues = Range(Required(values, "bufferView", valueContext),
                Integer(values, "byteOffset", valueContext, 0), sparseCount, elementSize, componentSize, valueContext, false, out _);
            sparseIndices = new int[sparseCount];
            int previous = -1;
            for (int i = 0; i < sparseCount; i++)
            {
                uint item = Unsigned(indexData.Span.Slice(i * indexSize, indexSize), indexType);
                if (item >= (uint)count || (long)item <= previous)
                    throw new InvalidDataException($"{indexContext}: entry {i} must increase strictly within accessor count {count}.");
                sparseIndices[i] = (int)item;
                previous = (int)item;
            }
        }
        return new Accessor(data, stride, count, components, componentType, normalized, sparseIndices, sparseValues);
    }

    ReadOnlyMemory<byte> Range(JsonElement reference, int offset, int count, int elementSize, int alignment,
        string context, bool allowStride, out int stride)
    {
        int viewIndex = Index(reference, context + " bufferView");
        context += $" bufferView {viewIndex}";
        JsonElement view = Entry(root, "bufferViews", viewIndex, context);
        if (refuseCompression && view.TryGetProperty("extensions", out JsonElement extensions)
            && (extensions.TryGetProperty("EXT_meshopt_compression", out _) || extensions.TryGetProperty("KHR_meshopt_compression", out _)))
            throw new InvalidDataException($"{context}: compressed geometry is not supported by rig-v1.");
        if (Integer(view, "buffer", context) != 0)
            throw new InvalidDataException($"{context}: buffer must be 0.");
        int viewOffset = Integer(view, "byteOffset", context, 0);
        int length = Integer(view, "byteLength", context);
        if ((long)viewOffset + length > binary.Length)
            throw new InvalidDataException($"{context}: range exceeds the embedded buffer.");
        stride = elementSize;
        if (view.TryGetProperty("byteStride", out _))
        {
            stride = Integer(view, "byteStride", context);
            if (!allowStride || stride < elementSize || stride > 252 || stride % 4 != 0)
                throw new InvalidDataException($"{context}: invalid byteStride.");
        }
        long required = (long)(count - 1) * stride + elementSize;
        if (offset % alignment != 0 || ((long)viewOffset + offset) % alignment != 0
            || offset > length || required > length - offset)
            throw new InvalidDataException($"{context}: accessor range or alignment is invalid.");
        return binary.Slice(viewOffset + offset, (int)required);
    }

    internal static JsonElement Required(JsonElement parent, string name, string context)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value))
            throw new InvalidDataException($"{context}: missing {name}.");
        return value;
    }

    internal static JsonElement Object(JsonElement parent, string name, string context)
    {
        JsonElement value = Required(parent, name, context);
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{context}: {name} must be an object.");
        return value;
    }

    internal static int Index(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) || number < 0)
            throw new InvalidDataException($"{context}: expected a non-negative integer.");
        return number;
    }

    internal static int Integer(JsonElement value, string name, string context, int? fallback = null)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement number))
            return Index(number, context + "." + name);
        if (fallback.HasValue) return fallback.Value;
        throw new InvalidDataException($"{context}: missing {name}.");
    }

    internal static JsonElement Entry(JsonElement parent, string name, int index, string context)
    {
        JsonElement values = Required(parent, name, context);
        if (values.ValueKind != JsonValueKind.Array || index >= values.GetArrayLength() || values[index].ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{context}: invalid {name} reference {index}.");
        return values[index];
    }

    static int ComponentSize(int type) => type == 5121 ? 1 : type == 5123 ? 2 : 4;

    static uint Unsigned(ReadOnlySpan<byte> data, int type) => type == 5121 ? data[0]
        : type == 5123 ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data);

    internal sealed class Accessor
    {
        readonly ReadOnlyMemory<byte> data;
        readonly int stride;
        readonly int components;
        readonly int componentType;
        readonly bool normalized;
        readonly int[] sparseIndices;
        readonly ReadOnlyMemory<byte> sparseValues;
        internal int Count { get; }
        internal int ComponentType => componentType;
        internal bool Normalized => normalized;
        internal int ElementSize => components * ComponentSize(componentType);

        internal Accessor(ReadOnlyMemory<byte> data, int stride, int count, int components, int componentType,
            bool normalized, int[] sparseIndices, ReadOnlyMemory<byte> sparseValues)
        {
            this.data = data;
            this.stride = stride;
            Count = count;
            this.components = components;
            this.componentType = componentType;
            this.normalized = normalized;
            this.sparseIndices = sparseIndices;
            this.sparseValues = sparseValues;
        }

        internal void ReadElement(int index, Span<float> result)
        {
            int componentSize = ComponentSize(componentType);
            ReadOnlySpan<byte> encoded = RawElement(index);
            for (int lane = 0; lane < components; lane++)
            {
                float value = 0;
                if (!encoded.IsEmpty)
                {
                    ReadOnlySpan<byte> component = encoded.Slice(lane * componentSize, componentSize);
                    value = componentType == 5126 ? BinaryPrimitives.ReadSingleLittleEndian(component) : Unsigned(component, componentType);
                    if (normalized) value /= componentType == 5121 ? 255f : 65535f;
                }
                result[lane] = value;
            }
        }

        // Return authored bytes after sparse selection, without decoding floats or normalization.
        // An empty span denotes glTF's implicit zero-filled base element.
        internal ReadOnlySpan<byte> RawElement(int index)
        {
            int sparseIndex = System.Array.BinarySearch(sparseIndices, index);
            return sparseIndex >= 0 ? sparseValues.Span.Slice(sparseIndex * ElementSize, ElementSize)
                : data.IsEmpty ? default : data.Span.Slice(index * stride, ElementSize);
        }

        internal uint UnsignedElement(int index)
        {
            ReadOnlySpan<byte> bytes = RawElement(index);
            return bytes.IsEmpty ? 0 : Unsigned(bytes, componentType);
        }
    }
}
