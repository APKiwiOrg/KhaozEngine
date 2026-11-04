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
        int declaredLength = Integer(root.GetProperty("buffers")[0], "byteLength", "buffer 0", jsonPath: "$.buffers[0]");
        // Invariant: public callers validate the declared length against BIN in Container first.
        if (declaredLength > binary.Length)
            throw new InvalidDataException("buffer 0: declared length exceeds the BIN chunk.");
        this.binary = binary[..declaredLength];
    }

    internal Accessor Read(JsonElement reference, string context, string type, string jsonPath, bool joints = false, bool weights = false, bool scalarIndices = false)
    {
        int index = Index(reference, context + " accessor", jsonPath);
        string path = $"$.accessors[{index}]";
        context += $" accessor {index}";
        JsonElement value = Entry(root, "accessors", index, context);
        if (!value.TryGetProperty("type", out JsonElement shape) || shape.ValueKind != JsonValueKind.String || shape.GetString() != type)
            throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.TypeMismatch, $"{context}: expected {type}.")
            {
                JsonPath = path + ".type",
                AccessorIndex = index,
                ExpectedType = type,
                ActualType = shape.ValueKind == JsonValueKind.String ? shape.GetString() : null,
            };
        int components = type == "MAT4" ? 16 : type == "VEC3" ? 3 : type == "SCALAR" ? 1 : 4;
        int componentType = Integer(value, "componentType", context, jsonPath: path);
        bool normalized = false;
        if (value.TryGetProperty("normalized", out JsonElement normalization))
        {
            if (normalization.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.NormalizedNotBoolean, $"{context}: normalized must be a boolean.")
                {
                    JsonPath = path + ".normalized",
                    AccessorIndex = index,
                };
            normalized = normalization.GetBoolean();
        }
        bool legal = scalarIndices ? (componentType is 5121 or 5123 or 5125) && !normalized
            : joints ? (componentType is 5121 or 5123) && !normalized
            : weights ? (componentType == 5126 && !normalized) || ((componentType is 5121 or 5123) && normalized)
            : componentType == 5126 && !normalized;
        if (!legal)
            throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.EncodingUnsupported, $"{context}: unsupported componentType or normalized encoding.")
            {
                JsonPath = path,
                ComponentType = componentType,
                Normalized = normalized,
            };
        int componentSize = ComponentSize(componentType);
        int elementSize = componentSize * components;
        int count = Integer(value, "count", context, jsonPath: path);
        if (count == 0)
            throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.CountNotPositive, $"{context}: count must be positive.")
            {
                JsonPath = path + ".count",
                Count = count,
            };
        int offset = Integer(value, "byteOffset", context, 0, path);
        ReadOnlyMemory<byte> data = default;
        int stride = elementSize;
        if (value.TryGetProperty("bufferView", out JsonElement viewReference))
            data = Range(viewReference, offset, count, elementSize, componentSize, context, path, true, out stride);
        else if (offset != 0)
            throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.OffsetWithoutBufferView, $"{context}: byteOffset requires a bufferView.")
            {
                JsonPath = path + ".byteOffset",
                ByteOffset = offset,
            };

        int[] sparseIndices = System.Array.Empty<int>();
        ReadOnlyMemory<byte> sparseValues = default;
        if (value.TryGetProperty("sparse", out JsonElement sparse))
        {
            string sparseContext = context + " sparse";
            string sparsePath = path + ".sparse";
            int sparseCount = Integer(sparse, "count", sparseContext, jsonPath: sparsePath);
            if (sparseCount == 0 || sparseCount > count)
                throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.SparseCountOutsideAccessor, $"{sparseContext}: count is outside the accessor.")
                {
                    JsonPath = sparsePath + ".count",
                    SparseCount = sparseCount,
                    AccessorCount = count,
                };
            JsonElement indices = Object(sparse, "indices", sparseContext, sparsePath);
            string indexContext = sparseContext + " indices";
            int indexType = Integer(indices, "componentType", indexContext, jsonPath: sparsePath + ".indices");
            if (indexType is not (5121 or 5123 or 5125))
                throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.SparseIndexEncodingUnsupported, $"{indexContext}: componentType must be unsigned byte, short or int.")
                {
                    JsonPath = sparsePath + ".indices.componentType",
                    ComponentType = indexType,
                };
            int indexSize = ComponentSize(indexType);
            ReadOnlyMemory<byte> indexData = Range(Required(indices, "bufferView", indexContext, sparsePath + ".indices"),
                Integer(indices, "byteOffset", indexContext, 0, sparsePath + ".indices"), sparseCount, indexSize, indexSize, indexContext, sparsePath + ".indices", false, out _);
            JsonElement values = Object(sparse, "values", sparseContext, sparsePath);
            string valueContext = sparseContext + " values";
            sparseValues = Range(Required(values, "bufferView", valueContext, sparsePath + ".values"),
                Integer(values, "byteOffset", valueContext, 0, sparsePath + ".values"), sparseCount, elementSize, componentSize, valueContext, sparsePath + ".values", false, out _);
            sparseIndices = new int[sparseCount];
            int previous = -1;
            for (int i = 0; i < sparseCount; i++)
            {
                uint item = Unsigned(indexData.Span.Slice(i * indexSize, indexSize), indexType);
                if (item >= (uint)count || (long)item <= previous)
                    throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.SparseIndexNotIncreasingInRange, $"{indexContext}: entry {i} must increase strictly within accessor count {count}.")
                    {
                        JsonPath = sparsePath + ".indices",
                        EntryIndex = i,
                        Index = item,
                        PreviousIndex = previous,
                        AccessorCount = count,
                    };
                sparseIndices[i] = (int)item;
                previous = (int)item;
            }
        }
        return new Accessor(data, stride, count, components, componentType, normalized, sparseIndices, sparseValues);
    }

    ReadOnlyMemory<byte> Range(JsonElement reference, int offset, int count, int elementSize, int alignment,
        string context, string jsonPath, bool allowStride, out int stride)
    {
        int viewIndex = Index(reference, context + " bufferView", jsonPath + ".bufferView");
        context += $" bufferView {viewIndex}";
        string path = $"$.bufferViews[{viewIndex}]";
        JsonElement view = Entry(root, "bufferViews", viewIndex, context);
        if (refuseCompression && view.TryGetProperty("extensions", out JsonElement extensions)
            && (extensions.TryGetProperty("EXT_meshopt_compression", out _) || extensions.TryGetProperty("KHR_meshopt_compression", out _)))
            throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.CompressionUnsupported, $"{context}: compressed geometry is not supported by rig-v1.")
            {
                JsonPath = path,
                BufferViewIndex = viewIndex,
                ExtensionName = extensions.TryGetProperty("EXT_meshopt_compression", out _) ? "EXT_meshopt_compression" : "KHR_meshopt_compression",
            };
        int bufferIndex = Integer(view, "buffer", context, jsonPath: path);
        if (bufferIndex != 0)
            throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.BufferIndexNotZero, $"{context}: buffer must be 0.")
            {
                JsonPath = path + ".buffer",
                BufferIndex = bufferIndex,
            };
        int viewOffset = Integer(view, "byteOffset", context, 0, path);
        int length = Integer(view, "byteLength", context, jsonPath: path);
        if ((long)viewOffset + length > binary.Length)
            throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.BufferViewOutsideBuffer, $"{context}: range exceeds the embedded buffer.")
            {
                JsonPath = path,
                ByteOffset = viewOffset,
                ByteLength = length,
                BufferLength = binary.Length,
            };
        stride = elementSize;
        if (view.TryGetProperty("byteStride", out _))
        {
            stride = Integer(view, "byteStride", context, jsonPath: path);
            if (!allowStride || stride < elementSize || stride > 252 || stride % 4 != 0)
                throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.ByteStrideInvalid, $"{context}: invalid byteStride.")
                {
                    JsonPath = path + ".byteStride",
                    ByteStride = stride,
                    ElementSize = elementSize,
                    StrideAllowed = allowStride,
                };
        }
        long required = (long)(count - 1) * stride + elementSize;
        if (offset % alignment != 0 || ((long)viewOffset + offset) % alignment != 0
            || offset > length || required > length - offset)
            throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.RangeOrAlignmentInvalid, $"{context}: accessor range or alignment is invalid.")
            {
                JsonPath = path,
                ByteOffset = offset,
                RequiredBytes = required,
                ViewLength = length,
                Alignment = alignment,
            };
        return binary.Slice(viewOffset + offset, (int)required);
    }

    internal static JsonElement Required(JsonElement parent, string name, string context, string jsonPath = "$")
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value))
            throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.PropertyMissing, $"{context}: missing {name}.")
            {
                JsonPath = jsonPath,
                PropertyName = name,
            };
        return value;
    }

    internal static JsonElement Object(JsonElement parent, string name, string context, string jsonPath = "$")
    {
        JsonElement value = Required(parent, name, context, jsonPath);
        if (value.ValueKind != JsonValueKind.Object)
            throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.ObjectRequired, $"{context}: {name} must be an object.")
            {
                JsonPath = jsonPath + "." + name,
                PropertyName = name,
            };
        return value;
    }

    internal static int Index(JsonElement value, string context, string jsonPath = "$")
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) || number < 0)
            throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.NonNegativeIntegerRequired, $"{context}: expected a non-negative integer.")
            {
                JsonPath = jsonPath,
            };
        return number;
    }

    internal static int Integer(JsonElement value, string name, string context, int? fallback = null, string jsonPath = "$")
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement number))
            return Index(number, context + "." + name, jsonPath + "." + name);
        if (fallback.HasValue) return fallback.Value;
        throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.PropertyMissing, $"{context}: missing {name}.")
        {
            JsonPath = jsonPath,
            PropertyName = name,
        };
    }

    internal static JsonElement Entry(JsonElement parent, string name, int index, string context)
    {
        JsonElement values = Required(parent, name, context);
        if (values.ValueKind != JsonValueKind.Array || index >= values.GetArrayLength() || values[index].ValueKind != JsonValueKind.Object)
            throw new AttachmentAccessorDataException(AttachmentAccessorRefusal.ReferenceInvalid, $"{context}: invalid {name} reference {index}.")
            {
                JsonPath = "$." + name,
                TableName = name,
                Index = index,
                EntryCount = values.ValueKind == JsonValueKind.Array ? values.GetArrayLength() : null,
            };
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
