using System;
using System.Buffers.Binary;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

internal static class AttachmentRefusalAssertions
{
    internal static bool ContainerPolicy(string name, Action validate, string source)
    {
        AttachmentGlbContainerRefusal? reason = name switch
        {
            "buffer-uri" or "external-buffer" or "data-buffer" or "image-uri" or "data-uri" or "external-image" or "data-image" => AttachmentGlbContainerRefusal.ExternalResourceUri,
            "missing-binary" or "short-binary" or "multiple-buffers" => AttachmentGlbContainerRefusal.EmbeddedBufferLengthMismatch,
            "invalid-json" => AttachmentGlbContainerRefusal.JsonMalformed,
            "duplicate-json" or "nested-duplicate" or "nested-duplicate-key" => AttachmentGlbContainerRefusal.DuplicateJsonProperty,
            "root-array" => AttachmentGlbContainerRefusal.JsonRootNotObject,
            "asset-version" => AttachmentGlbContainerRefusal.AssetVersionUnsupported,
            "two-buffers" => AttachmentGlbContainerRefusal.MultipleEmbeddedBuffers,
            "array-shape" => AttachmentGlbContainerRefusal.PropertyNotArray,
            "array-entry" => AttachmentGlbContainerRefusal.ArrayEntryNotObject,
            "value-shape" => AttachmentGlbContainerRefusal.ValueShapeInvalid,
            _ => null,
        };
        if (reason is null) return false;
        var error = Assert.Throws<AttachmentGlbContainerDataException>(validate);
        Assert.IsAssignableFrom<MalformedDataException>(error);
        Assert.Equal(reason.Value, error.Reason);
        Assert.Equal(source, error.SourceName);
        switch (reason.Value)
        {
            case AttachmentGlbContainerRefusal.ExternalResourceUri:
                Assert.Equal(name.Contains("buffer", StringComparison.Ordinal) ? "buffer" : "image", error.ResourceKind);
                break;
            case AttachmentGlbContainerRefusal.EmbeddedBufferLengthMismatch:
                Assert.Equal(name == "short-binary" ? 8u : name == "multiple-buffers" ? 0u : 4u, error.DeclaredLength);
                Assert.Equal(name == "missing-binary" ? -1 : name == "multiple-buffers" ? 352 : 4, error.BinaryLength);
                break;
            case AttachmentGlbContainerRefusal.DuplicateJsonProperty:
                Assert.Equal(name is "nested-duplicate" or "nested-duplicate-key" ? "x" : "version", error.PropertyName);
                Assert.Equal(name == "nested-duplicate-key" ? "$.extensions.VENDOR_test[\"a.b\"][0]" : name == "nested-duplicate" ? "$.extensions.VENDOR_test.nested[0]" : "$.asset", error.JsonPath);
                break;
            case AttachmentGlbContainerRefusal.AssetVersionUnsupported: Assert.Equal("1.0", error.AssetVersion); break;
            case AttachmentGlbContainerRefusal.MultipleEmbeddedBuffers: Assert.Equal(2, error.BufferCount); break;
            case AttachmentGlbContainerRefusal.PropertyNotArray: Assert.Equal("buffers", error.PropertyName); break;
            case AttachmentGlbContainerRefusal.ArrayEntryNotObject:
                Assert.Equal("buffers", error.PropertyName);
                Assert.Equal(0, error.EntryIndex);
                break;
            case AttachmentGlbContainerRefusal.JsonMalformed:
            case AttachmentGlbContainerRefusal.ValueShapeInvalid: Assert.NotNull(error.InnerException); break;
        }
        return true;
    }

    internal static void Envelope(string name, byte[] glb, Action validate)
    {
        var reason = name switch
        {
            "header" or "magic" or "version" or "total-length" => AttachmentGlbContainerRefusal.HeaderInvalid,
            "trailing-header" => AttachmentGlbContainerRefusal.ChunkHeaderTruncated,
            "json-not-first" => AttachmentGlbContainerRefusal.FirstChunkNotJson,
            "unexpected-chunk" => AttachmentGlbContainerRefusal.UnexpectedChunk,
            _ => AttachmentGlbContainerRefusal.ChunkLengthInvalid,
        };
        var error = Assert.Throws<AttachmentGlbContainerDataException>(validate);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(name + ".glb", error.SourceName);
        if (reason == AttachmentGlbContainerRefusal.HeaderInvalid)
        {
            Assert.Equal(glb.Length, error.ByteLength);
            Assert.Equal(name == "header" ? 0u : name == "version" ? 1u : 2u, error.Version);
            Assert.Equal(name == "header" ? null : name == "total-length" ? uint.MaxValue : (uint)glb.Length, error.DeclaredLength);
        }
        else if (reason == AttachmentGlbContainerRefusal.FirstChunkNotJson)
        {
            Assert.Equal(0x004e4942u, error.ChunkType);
            Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12)), error.ChunkLength);
        }
        else
        {
            int offset = name == "trailing-header" ? glb.Length - 4
                : name is "truncated-chunk" ? glb.Length - 11
                : name is "overflow-binary" or "unexpected-chunk" ? glb.Length - 12 : 12;
            Assert.Equal(offset, error.Offset);
            if (reason == AttachmentGlbContainerRefusal.UnexpectedChunk) Assert.Equal(42u, error.ChunkType);
            else
            {
                Assert.Equal(glb.Length - offset - (reason == AttachmentGlbContainerRefusal.ChunkHeaderTruncated ? 0 : 8), error.AvailableBytes);
                if (reason == AttachmentGlbContainerRefusal.ChunkLengthInvalid)
                    Assert.Equal(name is "overflow-chunk" or "overflow-binary" ? uint.MaxValue - 3 : name == "unaligned-chunk" ? 3u : 4u, error.DeclaredLength);
            }
        }
    }

    internal static bool Skinned(string name, Action validate)
    {
        if (ContainerPolicy(name, validate, "bad.glb")) return true;
        AttachmentHierarchyRefusal? hierarchy = name switch
        {
            "no-common-root" or "separate-joint-root" => AttachmentHierarchyRefusal.JointsHaveDifferentRoots,
            "hierarchy-cycle" or "self-cycle" or "detached-cycle" => AttachmentHierarchyRefusal.Cycle,
            "two-parents" or "repeated-child" => AttachmentHierarchyRefusal.MultipleParents,
            "children-object" => AttachmentHierarchyRefusal.ChildrenNotArray,
            "child-range" => AttachmentHierarchyRefusal.ChildIndexOutsideNodes,
            "non-object-node" => AttachmentHierarchyRefusal.NodeNotObject,
            _ => null,
        };
        if (hierarchy.HasValue)
        {
            var error = Assert.Throws<AttachmentHierarchyDataException>(validate);
            Assert.IsAssignableFrom<MalformedDataException>(error);
            Assert.Equal(hierarchy.Value, error.Reason);
            Assert.Equal("bad.glb", error.SourceName);
            int node = name == "detached-cycle" ? 5 : name is "self-cycle" or "non-object-node" ? 4
                : name is "two-parents" or "repeated-child" ? 1 : 0;
            if (hierarchy == AttachmentHierarchyRefusal.JointsHaveDifferentRoots)
            {
                Assert.Equal("$.skins[0].joints", error.JsonPath);
                Assert.Equal(0, error.ExpectedRootIndex);
                Assert.Equal(name == "no-common-root" ? 1 : 3, error.ActualRootIndex);
            }
            else
            {
                Assert.Equal(node, error.NodeIndex);
                Assert.Equal($"$.nodes[{node}]" + (hierarchy is AttachmentHierarchyRefusal.ChildrenNotArray or AttachmentHierarchyRefusal.ChildIndexOutsideNodes ? ".children" : ""), error.JsonPath);
                if (hierarchy == AttachmentHierarchyRefusal.MultipleParents) Assert.Equal(name == "two-parents" ? 4 : 0, error.ParentIndex);
                if (hierarchy == AttachmentHierarchyRefusal.ChildIndexOutsideNodes)
                {
                    Assert.Equal(99, error.ChildIndex);
                    Assert.Equal(5, error.NodeCount);
                }
            }
            return true;
        }
        AttachmentAccessorRefusal? accessor = name switch
        {
            "child-negative" or "child-fractional" or "child-string" or "child-null" or "child-overflow" or "accessor-reference" or "fractional-reference" or "negative-reference" or "overflow-view" or "overflow-view-length" or "accessor-offset" or "stride-overflow" or "sparse-offset" or "sparse-overflow" => AttachmentAccessorRefusal.NonNegativeIntegerRequired,
            "joint-type" or "joint-normalized" or "weight-type" or "weight-unnormalized" or "float-normalized" or "negative-joint" or "fractional-joint" => AttachmentAccessorRefusal.EncodingUnsupported,
            "joint-shape" or "weight-shape" => AttachmentAccessorRefusal.TypeMismatch,
            "missing-position" or "missing-accessors" or "later-joints" or "later-weights" => AttachmentAccessorRefusal.PropertyMissing,
            "short-view" or "overflow-count" or "bind-short" or "sparse-short-values" or "sparse-short-indices" => AttachmentAccessorRefusal.RangeOrAlignmentInvalid,
            "stride-short" => AttachmentAccessorRefusal.ByteStrideInvalid,
            "view-buffer" => AttachmentAccessorRefusal.BufferIndexNotZero,
            "view-reference" or "bind-reference" or "sparse-reference" or "joint-node-range" => AttachmentAccessorRefusal.ReferenceInvalid,
            "sparse-index-range" or "sparse-index-order" => AttachmentAccessorRefusal.SparseIndexNotIncreasingInRange,
            "sparse-index-type" => AttachmentAccessorRefusal.SparseIndexEncodingUnsupported,
            "sparse-count" => AttachmentAccessorRefusal.SparseCountOutsideAccessor,
            _ => null,
        };
        if (!accessor.HasValue) return false;
        var refusal = Assert.Throws<AttachmentAccessorDataException>(validate);
        Assert.IsAssignableFrom<MalformedDataException>(refusal);
        Assert.Equal(accessor.Value, refusal.Reason);
        Assert.Equal("bad.glb", refusal.SourceName);
        AssertAccessorIdentifiers(name, refusal);
        return true;
    }

    static void AssertAccessorIdentifiers(string name, AttachmentAccessorDataException error)
    {
        switch (error.Reason)
        {
            case AttachmentAccessorRefusal.EncodingUnsupported:
                Assert.Equal(name is "negative-joint" or "fractional-joint" or "joint-type" or "joint-normalized" ? "$.accessors[1]" : "$.accessors[2]", error.JsonPath);
                Assert.Equal(name is "negative-joint" or "fractional-joint" or "float-normalized" ? 5126 : name == "joint-type" ? 5122 : name == "weight-type" ? 5125 : 5121, error.ComponentType);
                Assert.Equal(name is "joint-normalized" or "float-normalized", error.Normalized);
                break;
            case AttachmentAccessorRefusal.TypeMismatch:
                int index = name == "joint-shape" ? 1 : 2;
                Assert.Equal(index, error.AccessorIndex);
                Assert.Equal($"$.accessors[{index}].type", error.JsonPath);
                Assert.Equal("VEC4", error.ExpectedType);
                Assert.Equal("VEC3", error.ActualType);
                break;
            case AttachmentAccessorRefusal.PropertyMissing:
                Assert.Equal(name == "missing-accessors" ? "accessors" : name == "later-joints" ? "JOINTS_0" : name == "later-weights" ? "WEIGHTS_0" : "POSITION", error.PropertyName);
                Assert.Equal(name == "missing-accessors" ? "$" : name is "later-joints" or "later-weights" ? "$.meshes[0].primitives[1].attributes" : "$.meshes[0].primitives[0].attributes", error.JsonPath);
                break;
            case AttachmentAccessorRefusal.BufferIndexNotZero:
                Assert.Equal("$.bufferViews[2].buffer", error.JsonPath);
                Assert.Equal(1, error.BufferIndex);
                break;
            case AttachmentAccessorRefusal.ByteStrideInvalid:
                Assert.Equal("$.bufferViews[2].byteStride", error.JsonPath);
                Assert.Equal(4, error.ByteStride);
                Assert.Equal(16, error.ElementSize);
                Assert.True(error.StrideAllowed);
                break;
            case AttachmentAccessorRefusal.SparseCountOutsideAccessor:
                Assert.Equal("$.accessors[2].sparse.count", error.JsonPath);
                Assert.Equal(int.MaxValue, error.SparseCount);
                Assert.Equal(3, error.AccessorCount);
                break;
            case AttachmentAccessorRefusal.SparseIndexEncodingUnsupported:
                Assert.Equal("$.accessors[2].sparse.indices.componentType", error.JsonPath);
                Assert.Equal(5126, error.ComponentType);
                break;
            case AttachmentAccessorRefusal.SparseIndexNotIncreasingInRange:
                Assert.Equal("$.accessors[2].sparse.indices", error.JsonPath);
                Assert.Equal(name == "sparse-index-range" ? 0 : 1, error.EntryIndex);
                Assert.Equal(name == "sparse-index-range" ? 3u : 1u, error.Index);
                Assert.Equal(name == "sparse-index-range" ? -1 : 1, error.PreviousIndex);
                Assert.Equal(3, error.AccessorCount);
                break;
            case AttachmentAccessorRefusal.ReferenceInvalid:
                Assert.Equal(name == "bind-reference" ? "accessors" : name == "joint-node-range" ? "nodes" : "bufferViews", error.TableName);
                Assert.Equal(name == "bind-reference" ? "$.accessors" : name == "joint-node-range" ? "$.nodes" : "$.bufferViews", error.JsonPath);
                Assert.Equal(99, error.Index);
                Assert.Equal(name == "bind-reference" || name == "view-reference" ? 4 : name == "joint-node-range" ? 5 : 6, error.EntryCount);
                break;
            case AttachmentAccessorRefusal.RangeOrAlignmentInvalid:
                int view = name is "bind-short" ? 3 : name == "sparse-short-indices" ? 4 : name == "sparse-short-values" ? 5 : 2;
                Assert.Equal($"$.bufferViews[{view}]", error.JsonPath);
                Assert.Equal(0, error.ByteOffset);
                Assert.Equal(name == "overflow-count" ? (long)int.MaxValue * 16 : name == "bind-short" ? 256 : name == "sparse-short-values" ? 16 : name == "sparse-short-indices" ? 2 : 48, error.RequiredBytes);
                Assert.Equal(name == "bind-short" ? 255 : name == "sparse-short-values" ? 15 : name == "sparse-short-indices" ? 1 : name == "short-view" ? 47 : 48, error.ViewLength);
                Assert.Equal(name == "sparse-short-indices" ? 2 : 4, error.Alignment);
                break;
            case AttachmentAccessorRefusal.NonNegativeIntegerRequired:
                Assert.Equal(name switch
                {
                    "accessor-reference" or "fractional-reference" => "$.meshes[0].primitives[0].attributes.JOINTS_0",
                    "negative-reference" => "$.meshes[0].primitives[0].attributes.WEIGHTS_0",
                    "overflow-view" => "$.bufferViews[2].byteOffset",
                    "overflow-view-length" => "$.bufferViews[2].byteLength",
                    "accessor-offset" => "$.accessors[2].byteOffset",
                    "stride-overflow" => "$.bufferViews[2].byteStride",
                    "sparse-offset" => "$.accessors[2].sparse.values.byteOffset",
                    "sparse-overflow" => "$.accessors[2].sparse.indices.byteOffset",
                    _ => "$.nodes[0].children[0]",
                }, error.JsonPath);
                break;
        }
    }

    internal static bool Rig(string name, Action validate)
    {
        if (name is "bad-parent" or "cycle")
        {
            var error = Assert.Throws<AttachmentHierarchyDataException>(validate);
            Assert.Equal(name == "cycle" ? AttachmentHierarchyRefusal.Cycle : AttachmentHierarchyRefusal.MultipleParents, error.Reason);
            Assert.Equal("body.glb", error.SourceName);
            Assert.Equal(name == "cycle" ? 0 : 1, error.NodeIndex);
            Assert.Equal(name == "cycle" ? "$.nodes[0]" : "$.nodes[1]", error.JsonPath);
            if (name == "bad-parent") Assert.Equal(2, error.ParentIndex);
            return true;
        }
        if (name is not ("no-skin" or "short-view" or "unpaired-set")) return false;
        var accessor = Assert.Throws<AttachmentAccessorDataException>(validate);
        Assert.Equal(name == "short-view" ? AttachmentAccessorRefusal.RangeOrAlignmentInvalid : AttachmentAccessorRefusal.PropertyMissing, accessor.Reason);
        Assert.Equal("body.glb", accessor.SourceName);
        if (name == "short-view")
        {
            Assert.Equal("$.bufferViews[0]", accessor.JsonPath);
            Assert.Equal(0, accessor.ByteOffset);
            Assert.Equal(36, accessor.RequiredBytes);
            Assert.Equal(1, accessor.ViewLength);
            Assert.Equal(4, accessor.Alignment);
        }
        else
        {
            Assert.Equal(name == "no-skin" ? "$" : "$.meshes[0].primitives[0].attributes", accessor.JsonPath);
            Assert.Equal(name == "no-skin" ? "skins" : "WEIGHTS_1", accessor.PropertyName);
        }
        return true;
    }
}
