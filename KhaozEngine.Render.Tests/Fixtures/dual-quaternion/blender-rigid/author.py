"""Public synthetic Preserve Volume evidence. Run in factory-startup headless Blender."""

import argparse
import hashlib
import json
import math
from pathlib import Path
import struct
import sys

import bpy
import io_scene_gltf2
from mathutils import Matrix, Quaternion, Vector


# Blender column vectors (X,Y,Z) become glTF/engine (X,Z,-Y). Stored MAT4 words
# are column-major glTF, equivalently row-major System.Numerics transposes.
CONVERSION = Matrix(((1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))
NAMES = ["joint_0", "joint_1", "joint_2", "joint_3", "joint_4"]
FRAMES = (1, 13, 25, 37)
CASES = ("two-influence", "four-unambiguous", "pivot", "ambiguous-four")
WEIGHTS = ((.25, .75, 0, 0, 0), (.125, .1875, .3125, .375, 0),
           (0, 0, 1, 0, 0), (.125, .1875, .3125, .375, 0))


def words(values):
    return [struct.unpack("<I", struct.pack("<f", value))[0] for value in values]


def matrix_words(matrix):
    return words([matrix[row][column] for column in range(4) for row in range(4)])


def engine_position(position):
    return list((CONVERSION @ position.to_4d()).to_3d())


def engine_matrix(matrix):
    return CONVERSION @ matrix @ CONVERSION.inverted()


def mesh_object(name, coordinates, weights, rig, order=range(5)):
    data = bpy.data.meshes.new(name)
    data.from_pydata(coordinates, [], [(i, i + 1, i + 2) for i in range(0, len(coordinates), 3)])
    data.update()
    obj = bpy.data.objects.new(name, data)
    bpy.context.collection.objects.link(obj)
    for joint in order:
        group = obj.vertex_groups.new(name=NAMES[joint])
        for vertex, row in enumerate(weights):
            if row[joint] > 0:
                group.add([vertex], row[joint], "REPLACE")
    modifier = obj.modifiers.new("Preserve Volume", "ARMATURE")
    modifier.object = rig
    modifier.use_deform_preserve_volume = True
    modifier.use_vertex_groups = True
    modifier.use_bone_envelopes = False
    return obj


def evaluated_positions(obj):
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = evaluated.to_mesh()
    try:
        return [engine_position(evaluated.matrix_world @ vertex.co) for vertex in mesh.vertices]
    finally:
        evaluated.to_mesh_clear()


def read_glb(path):
    payload = path.read_bytes()
    magic, version, length = struct.unpack_from("<III", payload)
    assert (magic, version, length) == (0x46546C67, 2, len(payload))
    json_length, json_type = struct.unpack_from("<II", payload, 12)
    assert json_type == 0x4E4F534A
    root = json.loads(payload[20:20 + json_length])
    binary_length, binary_type = struct.unpack_from("<II", payload, 20 + json_length)
    assert binary_type == 0x004E4942
    binary = payload[28 + json_length:28 + json_length + binary_length]
    return root, binary


def accessor(root, binary, index):
    item = root["accessors"][index]
    view = root["bufferViews"][item["bufferView"]]
    count = {"SCALAR": 1, "VEC3": 3, "VEC4": 4, "MAT4": 16}[item["type"]]
    code = {5121: "B", 5123: "H", 5125: "I", 5126: "f"}[item["componentType"]]
    size = struct.calcsize("<" + code * count)
    offset = view.get("byteOffset", 0) + item.get("byteOffset", 0)
    return [list(struct.unpack_from("<" + code * count, binary, offset + i * view.get("byteStride", size)))
            for i in range(item["count"])]


def exported_joint_words(root, binary, time):
    """Sample actual exported keys, including the exporter's bone-space convention."""
    assert len(root["animations"]) == 1
    animation = root["animations"][0]
    overrides = {}
    for channel in animation["channels"]:
        sampler = animation["samplers"][channel["sampler"]]
        times = accessor(root, binary, sampler["input"])
        key = min(range(len(times)), key=lambda i: abs(times[i][0] - time))
        assert abs(times[key][0] - time) < 1e-6, (time, times)
        overrides[(channel["target"]["node"], channel["target"]["path"])] = accessor(
            root, binary, sampler["output"])[key]
    parents = {child: i for i, node in enumerate(root["nodes"]) for child in node.get("children", [])}
    cache = {}

    def world(index):
        if index in cache:
            return cache[index]
        node = root["nodes"][index]
        assert "matrix" not in node
        translation = overrides.get((index, "translation"), node.get("translation", [0, 0, 0]))
        rotation = overrides.get((index, "rotation"), node.get("rotation", [0, 0, 0, 1]))
        scale = overrides.get((index, "scale"), node.get("scale", [1, 1, 1]))
        local = Matrix.LocRotScale(Vector(translation), Quaternion((rotation[3], *rotation[:3])), Vector(scale))
        cache[index] = world(parents[index]) @ local if index in parents else local
        return cache[index]

    return [matrix_words(world(index)) for index in root["skins"][0]["joints"]]


def author(output):
    output.mkdir(parents=True, exist_ok=True)
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    scene = bpy.context.scene
    scene.render.fps = 24
    scene.frame_start, scene.frame_end = 1, 37
    rig_data = bpy.data.armatures.new("Public synthetic rigid rig")
    rig = bpy.data.objects.new("Public synthetic rigid rig", rig_data)
    bpy.context.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    heads = ((2, -3, 1), (-1, .5, 2), (1, 2, -.5), (-2, -1, 1.5), (0, 1, 1))
    for name, head in zip(NAMES, heads):
        bone = rig_data.edit_bones.new(name)
        bone.head = head
        bone.tail = Vector(head) + Vector((0, 1, 0))
    rig_data.edit_bones[NAMES[4]].parent = rig_data.edit_bones[NAMES[3]]
    bpy.ops.object.mode_set(mode="OBJECT")

    coordinates = [(1, 0, 0), (1, .25, 0), (1, 0, .25),
                   (-1, 1, .5), (-.75, 1, .5), (-1, 1.25, .5),
                   heads[2], (2, 2, -.5), (1, 2, .5),
                   (2, .5, 1), (2.25, .5, 1), (2, .75, 1)]
    vertex_weights = [WEIGHTS[i // 3] for i in range(12)]
    source = mesh_object("rigid", coordinates, vertex_weights, rig)
    reordered = mesh_object("reordered-order-witness", coordinates, vertex_weights, rig, (3, 2, 1, 0, 4))
    five = mesh_object("outside-five-positive", coordinates[:3], [(.125, .1875, .25, .3125, .125)] * 3, rig)
    modified = mesh_object("outside-additional-modifier", coordinates[:3], vertex_weights[:3], rig)
    twist = modified.modifiers.new("Outside promise twist", "SIMPLE_DEFORM")
    twist.deform_method, twist.angle = "TWIST", .4
    for obj in (reordered, five, modified):
        obj.hide_render = True
        obj["parity_disposition"] = "outside-promise" if obj != reordered else "order-divergence-witness"
    axes = ((0, 0, 1), (1, 0, 0), (0, 1, 0), (1, 1, 1), (0, 0, 1))
    angles = {1: (0, 0, 0, 0, 0), 13: (20, -35, 55, -15, 0),
              25: (-50, 25, -40, 60, 0), 37: (0, 140, 220, 310, 0), 49: (0, 0, 0, 30, 45)}
    for frame, pose_angles in angles.items():
        scene.frame_set(frame)
        for joint, angle in enumerate(pose_angles):
            bone = rig.pose.bones[NAMES[joint]]
            bone.rotation_mode = "QUATERNION"
            axis = (0, 0, 1) if frame in (37, 49) else axes[joint]
            bone.rotation_quaternion = Quaternion(Vector(axis).normalized(), math.radians(angle))
            bone.location = (0, 0, 0) if frame == 1 else (joint * .03, -.02 * joint, .01 * joint)
            bone.scale = (1.5, .75, 1) if frame == 49 and joint == 3 else (1, 1, 1)
            for channel in ("rotation_quaternion", "location", "scale"):
                bone.keyframe_insert(data_path=channel, frame=frame)

    samples = []
    for frame in FRAMES:
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        sample = {"frame": frame, "timeSeconds": (frame - 1) / 24,
                  "kind": "ambiguous" if frame == 37 else "parity",
                  "blenderPositions": evaluated_positions(source)}
        if frame == 37:
            sample["reorderedBlenderPositions"] = evaluated_positions(reordered)
            distance = max((Vector(a) - Vector(b)).length for a, b in zip(
                sample["blenderPositions"][9:], sample["reorderedBlenderPositions"][9:]))
            assert distance > .1, distance
            sample["divergentOutcome"] = {"classification": "order-divergence-outside-matching-inputs",
                                          "maxPositionDistance": distance}
        samples.append(sample)

    scene.frame_set(49)
    bpy.context.view_layer.update()
    excluded = []
    for name, joint in (("scale", 3), ("shear", 4)):
        bone = rig.pose.bones[NAMES[joint]]
        composed = engine_matrix(bone.matrix @ bone.bone.matrix_local.inverted())
        excluded.append({"name": name, "disposition": "outside-promise", "frame": 49,
                         "reason": "Nonrigid composed pose exceeds the rigid Gram allowance.",
                         "composedWords": matrix_words(composed)})
    excluded += [{"name": "five-positive", "disposition": "outside-promise",
                  "reason": "Five positive source influences must be refused before export reduction.",
                  "sourceWeights": [group.weight for group in five.data.vertices[0].groups]},
                 {"name": "additional-modifier", "disposition": "outside-promise",
                  "reason": "The additional Simple Deform modifier is excluded from rigid Armature parity.",
                  "modifierStack": [modifier.type for modifier in modified.modifiers]}]

    scene.frame_set(1)
    bpy.ops.object.select_all(action="DESELECT")
    source.select_set(True)
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.wm.save_as_mainfile(filepath=str(output / "source.blend"))
    export_options = {"export_format": "GLB", "use_selection": True, "export_yup": True,
                      "export_apply": False, "export_skins": True, "export_all_influences": True,
                      "export_animations": True, "export_animation_mode": "SCENE",
                      "export_anim_scene_split_object": False, "export_frame_range": True,
                      "export_force_sampling": True, "export_frame_step": 1,
                      "export_anim_slide_to_zero": True, "export_optimize_animation_size": False,
                      "export_rest_position_armature": True, "export_current_frame": False}
    bpy.ops.export_scene.gltf(filepath=str(output / "rigid.glb"), **export_options)
    root, binary = read_glb(output / "rigid.glb")
    assert len(root["meshes"]) == len(root["skins"]) == 1
    primitive = root["meshes"][0]["primitives"]
    assert len(primitive) == 1
    attributes = primitive[0]["attributes"]
    assert "JOINTS_1" not in attributes and "WEIGHTS_1" not in attributes
    positions = accessor(root, binary, attributes["POSITION"])
    joints = accessor(root, binary, attributes["JOINTS_0"])
    weights = accessor(root, binary, attributes["WEIGHTS_0"])
    joint_names = [root["nodes"][i]["name"] for i in root["skins"][0]["joints"]]
    vertices = []
    for vertex in source.data.vertices:
        position = engine_position(vertex.co)
        matches = [i for i, value in enumerate(positions) if (Vector(value) - Vector(position)).length < 1e-6]
        assert len(matches) == 1, matches
        index = matches[0]
        groups = list(vertex.groups)
        order = [joint_names.index(source.vertex_groups[group.group].name) for group in groups]
        source_weights = [group.weight for group in groups]
        assert len(order) <= 4
        vertices.append({"case": CASES[vertex.index // 3], "sourceVertex": vertex.index,
                         "exportedVertex": index, "sourcePosition": position,
                         "sourceOrder": order + [0] * (4 - len(order)),
                         "sourceWeights": source_weights + [0] * (4 - len(order)),
                         "exportedOrder": joints[index], "exportedWeights": weights[index]})
    for sample in samples:
        sample["jointWorldWords"] = exported_joint_words(root, binary, sample["timeSeconds"])
    exporter_file = Path(io_scene_gltf2.__file__).parent / "blender/exp/primitive_extract.py"
    expected = {"schemaVersion": 1,
                "provenance": {"fixture": "public-synthetic-only", "blenderVersion": bpy.app.version_string,
                               "blenderBuildHash": bpy.app.build_hash.decode(),
                               "exporterVersion": io_scene_gltf2.get_version_string(),
                               "exporterPrimitiveExtractSha256": hashlib.sha256(exporter_file.read_bytes()).hexdigest(),
                               "glbGenerator": root["asset"]["generator"],
                               "glbSha256": hashlib.sha256((output / "rigid.glb").read_bytes()).hexdigest(),
                               "exportOptions": export_options, "fps": 24,
                               "jointWorldWordsSource": "actual exported animation keys composed through the node hierarchy",
                               "blenderPositionsSource": "evaluated Preserve Volume Armature modifier, then coordinate conversion"},
                "coordinateTransform": {"description": "Blender (X,Y,Z) to engine/glTF (X,Z,-Y), no unit scaling",
                                        "rowMatrixWords": matrix_words(CONVERSION)},
                "sourceVertexGroupOrder": [group.name for group in source.vertex_groups],
                "reorderedVertexGroupOrder": [group.name for group in reordered.vertex_groups],
                "jointNames": joint_names,
                "inverseBindWords": [words(row) for row in accessor(root, binary, root["skins"][0]["inverseBindMatrices"])],
                "vertices": vertices, "samples": samples, "excluded": excluded}
    (output / "expected.json").write_text(json.dumps(expected, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"blender": bpy.app.version_string, "exporter": io_scene_gltf2.get_version_string(),
                      "vertices": len(vertices), "samples": len(samples),
                      "ambiguousMaxDistance": samples[-1]["divergentOutcome"]["maxPositionDistance"]}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    author(arguments.output.resolve())
