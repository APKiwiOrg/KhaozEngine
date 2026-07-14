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
//   4. dilate (alpha-bleed) each baseColor texture that has transparency: flood
//      the RGB of the visible (alpha >= threshold) texels outward into the fully
//      transparent texels, leaving alpha untouched. The Quaternius leaf-card
//      textures store BLACK rgb under their transparent texels, so plain box-filter
//      mip generation (and bilinear at the cutout edge) folds that black into the
//      leaf colour: dark fringes and foliage that darkens with distance. Bleeding
//      leaf colour into those texels makes every mip average leaf-on-leaf, killing
//      the fringe. Alpha is preserved to the bit, so the runtime alpha-cutout
//      (glTF alphaMode=MASK) discards exactly the same silhouette as before.
//      Only the colour that survives at the edges changes. Fully-opaque textures
//      (bark, rock) have no transparent texels, so this is a no-op for them.
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

// The only three extensions this recipe expects to still be declared (but unused) after dequantize +
// textureCompress: the decode/re-encode steps consume what they represent (compressed mesh data,
// quantized attributes, webp images) without removing the now-dangling extension declaration. Disposing
// is scoped to exactly this allowlist, not "everything used", so a source glb carrying some OTHER
// extension (draco, a material extension, KHR_texture_transform, ...) is never silently dropped.
const EXPECTED_DISPOSED_EXTENSIONS = new Set([
  'EXT_meshopt_compression',
  'KHR_mesh_quantization',
  'EXT_texture_webp',
]);

// Alpha-bleed threshold: a texel is a colour "source" (kept, and seeds the flood) when its alpha is at or
// above this. Below it the texel is a "hole" whose RGB gets overwritten by the flooded neighbour colour.
// 16/255 (~0.06) sits well under the assets' 0.2 MASK cutoff, so every texel that survives the runtime
// cutout is a source, and only the fully-transparent black interior is refilled.
const ALPHA_BLEED_THRESHOLD = 16;
// Safety cap on flood iterations (each spreads colour one texel-ring). 512 fully covers a 512x512 leaf
// atlas. The flood normally converges long before this because opaque leaves are scattered throughout.
const ALPHA_BLEED_MAX_ITERS = 512;

// In-place RGB flood dilation over a raw RGBA buffer: each hole texel (alpha < threshold) takes the mean
// RGB of its already-known 8-neighbours, iterating outward until no hole borders a known texel (or the cap).
// Alpha is never touched, so the alpha-cutout silhouette is unchanged, and only transparent RGB is refilled.
// Deterministic (fixed neighbour order, fixed iteration), so a re-bake round-trips bit-for-bit.
function dilateRgb(data, w, h, threshold, maxIters) {
  const n = w * h;
  const known = new Uint8Array(n);
  let holes = 0;
  for (let i = 0; i < n; i += 1) {
    if (data[i * 4 + 3] >= threshold) known[i] = 1; else holes += 1;
  }
  if (holes === 0 || holes === n) return { changed: false, holes };   // all opaque, or nothing to seed from
  let remaining = holes;
  for (let it = 0; it < maxIters && remaining > 0; it += 1) {
    const fillIdx = [];
    const fillRgb = [];
    for (let y = 0; y < h; y += 1) {
      for (let x = 0; x < w; x += 1) {
        const idx = y * w + x;
        if (known[idx]) continue;
        let r = 0, g = 0, b = 0, c = 0;
        // Bounds-checked 8-neighbour gather of already-known texels.
        for (let dy = -1; dy <= 1; dy += 1) {
          const ny = y + dy;
          if (ny < 0 || ny >= h) continue;
          for (let dx = -1; dx <= 1; dx += 1) {
            if (dx === 0 && dy === 0) continue;
            const nx = x + dx;
            if (nx < 0 || nx >= w) continue;
            const nidx = ny * w + nx;
            if (!known[nidx]) continue;
            r += data[nidx * 4]; g += data[nidx * 4 + 1]; b += data[nidx * 4 + 2]; c += 1;
          }
        }
        if (c > 0) {
          fillIdx.push(idx);
          fillRgb.push(Math.round(r / c), Math.round(g / c), Math.round(b / c));
        }
      }
    }
    if (fillIdx.length === 0) break;   // no hole borders a known texel (disconnected region), so stop
    for (let j = 0; j < fillIdx.length; j += 1) {
      const idx = fillIdx[j];
      data[idx * 4] = fillRgb[j * 3];
      data[idx * 4 + 1] = fillRgb[j * 3 + 1];
      data[idx * 4 + 2] = fillRgb[j * 3 + 2];
      known[idx] = 1;
    }
    remaining -= fillIdx.length;
  }
  return { changed: true, holes };
}

// Alpha-bleed every unique baseColor texture that has transparency. Runs after the webp->png transform, so
// it reads/writes PNG. Preserves alpha exactly (runtime cutout silhouette unchanged), refilling only the RGB of
// transparent texels so mip/bilinear averaging pulls leaf colour, not the black stored under the leaves.
async function alphaBleedBaseColor(document) {
  const seen = new Set();
  const results = [];
  for (const mat of document.getRoot().listMaterials()) {
    const info = mat.getBaseColorTextureInfo();       // presence check without pulling image bytes twice
    const tex = mat.getBaseColorTexture();
    if (!tex || info === null || seen.has(tex)) continue;
    seen.add(tex);
    const img = tex.getImage();
    if (!img) continue;
    const { data, info: meta } = await sharp(Buffer.from(img)).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
    const { changed, holes } = dilateRgb(data, meta.width, meta.height, ALPHA_BLEED_THRESHOLD, ALPHA_BLEED_MAX_ITERS);
    if (!changed) { results.push({ name: mat.getName(), holes: 0, bled: false }); continue; }
    const png = await sharp(data, { raw: { width: meta.width, height: meta.height, channels: 4 } }).png().toBuffer();
    tex.setImage(new Uint8Array(png));
    tex.setMimeType('image/png');
    results.push({ name: mat.getName(), holes, bled: true });
  }
  return results;
}

async function bakeOne(io, sourcePath, outPath) {
  const document = await io.read(sourcePath);

  await document.transform(
    dequantize(),
    textureCompress({ encoder: sharp, targetFormat: 'png', formats: /webp/ }),
  );

  const bleed = await alphaBleedBaseColor(document);

  // Dispose only the allowlisted extensions left dangling by the transform above, so the output is
  // plain glTF 2.0 with no extensions. Fail loudly (rather than silently dropping unknown extension
  // data) if the source carried anything else this recipe was never designed to strip.
  for (const ext of document.getRoot().listExtensionsUsed()) {
    if (!EXPECTED_DISPOSED_EXTENSIONS.has(ext.extensionName)) {
      throw new Error(
        `${path.basename(sourcePath)}: unexpected extension '${ext.extensionName}' survived dequantize/textureCompress. ` +
        `This recipe only knows how to strip ${[...EXPECTED_DISPOSED_EXTENSIONS].join(', ')}. ` +
        'Extend EXPECTED_DISPOSED_EXTENSIONS (and verify the bake is still correct) before re-running.',
      );
    }
    ext.dispose();
  }

  const remaining = document.getRoot().listExtensionsUsed();
  if (remaining.length > 0) {
    throw new Error(
      `${path.basename(sourcePath)}: ${remaining.length} extension(s) still present after dispose: ` +
      `${remaining.map((ext) => ext.extensionName).join(', ')}.`,
    );
  }

  fs.mkdirSync(path.dirname(outPath), { recursive: true });
  await io.write(outPath, document);

  return report(document, sourcePath, outPath, bleed);
}

function report(document, sourcePath, outPath, bleed) {
  const root = document.getRoot();
  const materials = root.listMaterials();
  const textures = root.listTextures();
  const extensionNames = [...new Set(
    [...root.listExtensionsUsed(), ...root.listExtensionsRequired()].map((ext) => ext.extensionName),
  )];
  const imageFormats = [...new Set(textures.map((tex) => tex.getMimeType()).filter(Boolean))];
  const inSize = fs.statSync(sourcePath).size;
  const outSize = fs.statSync(outPath).size;
  // Alpha-mode/cutoff per material: the runtime alpha-cutout depends on these surviving the bake, so print
  // them (a MASK leaf material that lost its cutoff would silently render as a solid quad again).
  const alpha = materials.map((m) => `${m.getName()}=${m.getAlphaMode()}${m.getAlphaMode() === 'MASK' ? `(${m.getAlphaCutoff()})` : ''}`);
  const bled = (bleed || []).filter((b) => b.bled);

  console.log(`${path.basename(sourcePath)} -> ${path.basename(outPath)}`);
  console.log(`  materials: ${materials.length}, images: ${textures.length}, image formats: ${imageFormats.join(', ') || 'none'}`);
  console.log(`  alphaModes: ${alpha.join(', ')}`);
  console.log(`  alpha-bled: ${bled.length ? bled.map((b) => `${b.name}(${b.holes} texels)`).join(', ') : 'none'}`);
  console.log(`  extensions: ${extensionNames.length ? extensionNames.join(', ') : 'none'}`);
  console.log(`  size: ${inSize} bytes -> ${outSize} bytes`);

  return { materials: materials.length, textures: textures.length, imageFormats, extensionNames, inSize, outSize, alpha, bled };
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
