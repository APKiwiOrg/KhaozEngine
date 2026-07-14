#!/usr/bin/env node
// tools/kit-bake/bake.mjs
//
// Bakes "textures-ON" prop glbs for KhaozEngine from meshopt-compressed,
// KHR_mesh_quantization + EXT_texture_webp glTF sources into plain glb files
// that KhaozEngine.Render3D.GltfLoader (SharpGLTF-based) can load with real
// per-material textures.
//
// Recipe (matches KhaozEngine.Showcase/assets/props/CREDITS.md, "Textures-ON
// re-ingest" section):
//   1. decode meshopt (implicit on read, once EXT_meshopt_compression is
//      registered with a decoder) and dequantize (drops KHR_mesh_quantization,
//      restores float POSITION/NORMAL).
//   2. re-encode every EXT_texture_webp image to PNG (SharpGLTF cannot decode
//      webp) and drop the EXT_texture_webp extension.
//   3. keep baseColorTexture / normalTexture / metallicRoughnessTexture as-is
//      (no flattening to a factor). Images are embedded in the output glb.
//
// Usage:
//   node bake.mjs --input <sourceDir|sourceFile> --out <outDir> --map <mapping.json>
//
// mapping.json is a flat object of { "<sourceBaseName>": "<kitId>" }. When
// --input is a directory, each key resolves to "<sourceBaseName>.glb" inside
// it. When --input is a single file, mapping must contain exactly one entry
// and the source file is used as-is. Output files are written as
// "<outDir>/<kitId>.glb". See README.md for the mapping used for each round
// and foliage-map.json for this round's concrete mapping.
//
// Idempotent: every run reads fresh from the source and overwrites the
// output, no state is carried between runs.

import { NodeIO } from '@gltf-transform/core';
import { ALL_EXTENSIONS } from '@gltf-transform/extensions';
import { dequantize, textureCompress } from '@gltf-transform/functions';
import { MeshoptDecoder } from 'meshoptimizer';
import sharp from 'sharp';
import fs from 'node:fs';
import path from 'node:path';

function parseArgs(argv) {
  const args = { input: null, out: null, map: null };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--input') args.input = argv[(i += 1)];
    else if (arg === '--out') args.out = argv[(i += 1)];
    else if (arg === '--map') args.map = argv[(i += 1)];
    else throw new Error(`Unrecognized argument: ${arg}`);
  }
  if (!args.input || !args.out || !args.map) {
    throw new Error('Usage: bake.mjs --input <dir|file> --out <dir> --map <mapping.json>');
  }
  return args;
}

async function bakeOne(io, sourcePath, outPath) {
  const document = await io.read(sourcePath);

  await document.transform(
    dequantize(),
    textureCompress({ encoder: sharp, targetFormat: 'png', formats: /webp/ }),
  );

  // The decode + re-encode above leaves EXT_meshopt_compression,
  // EXT_texture_webp, and KHR_mesh_quantization declared but unused (nothing
  // in the document still references compressed/quantized/webp data). Drop
  // them explicitly so the output is plain glTF 2.0 with no extensions.
  for (const ext of document.getRoot().listExtensionsUsed()) {
    ext.dispose();
  }

  fs.mkdirSync(path.dirname(outPath), { recursive: true });
  await io.write(outPath, document);

  return report(document, sourcePath, outPath);
}

function report(document, sourcePath, outPath) {
  const root = document.getRoot();
  const materials = root.listMaterials();
  const textures = root.listTextures();
  const extensionNames = [...new Set(
    [...root.listExtensionsUsed(), ...root.listExtensionsRequired()].map((ext) => ext.extensionName),
  )];
  const imageFormats = [...new Set(textures.map((tex) => tex.getMimeType()).filter(Boolean))];
  const inSize = fs.statSync(sourcePath).size;
  const outSize = fs.statSync(outPath).size;

  console.log(`${path.basename(sourcePath)} -> ${path.basename(outPath)}`);
  console.log(`  materials: ${materials.length}, images: ${textures.length}, image formats: ${imageFormats.join(', ') || 'none'}`);
  console.log(`  extensions: ${extensionNames.length ? extensionNames.join(', ') : 'none'}`);
  console.log(`  size: ${inSize} bytes -> ${outSize} bytes`);

  return { materials: materials.length, textures: textures.length, imageFormats, extensionNames, inSize, outSize };
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const mapping = JSON.parse(fs.readFileSync(args.map, 'utf8'));
  const entries = Object.entries(mapping);
  if (entries.length === 0) {
    throw new Error(`Mapping file has no entries: ${args.map}`);
  }

  const inputStat = fs.statSync(args.input);
  const inputIsDir = inputStat.isDirectory();
  if (!inputIsDir && entries.length !== 1) {
    throw new Error('When --input is a single file, --map must contain exactly one source-to-kit-id entry.');
  }

  await MeshoptDecoder.ready;
  const io = new NodeIO()
    .registerExtensions(ALL_EXTENSIONS)
    .registerDependencies({ 'meshopt.decoder': MeshoptDecoder });

  for (const [sourceName, kitId] of entries) {
    const sourcePath = inputIsDir ? path.join(args.input, `${sourceName}.glb`) : args.input;
    if (!fs.existsSync(sourcePath)) {
      throw new Error(`Source not found: ${sourcePath}`);
    }
    const outPath = path.join(args.out, `${kitId}.glb`);
    await bakeOne(io, sourcePath, outPath);
  }
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
