# FBX interchange

The FBX module is a pure-managed parser and scene importer. It has no native runtime dependencies and does not serialize .AuraAsset files.

## Entry points

- FBXDocument.Parse(ReadOnlyMemory<byte>) detects binary or ASCII FBX.
- FBXDocument.ParseBinary and ParseText select a specific parser.
- FBXDocument.ParseAsync(Stream) supports asynchronous stream input. The in-memory overloads are synchronous internally and do not copy the caller's input buffer.
- FBXImporter.Import(FBXDocument) converts the parsed object/connection graph to FBXImportedScene.

The document model preserves the FBX node tree and typed property arrays. The imported scene model is an intermediate, in-memory interchange representation for later Auralis asset conversion/serialization. Its public geometry and transform data use the engine-owned `Vector2D`, `Vector3D`, `Vector4D`, `Quaternion`, and `Matrix` types so consumers do not need to translate the scene into a parallel set of BCL math structs.

## Coordinate convention

Imported geometry and transforms are converted to the Unreal-style convention:

- Left-handed coordinates.
- +X forward, +Y right, +Z up.
- Distances in centimeters.

The importer reads CoordAxis, UpAxis, FrontAxis, their sign properties, and UnitScaleFactor from GlobalSettings. The default FBX axis metadata is used when these settings are absent or invalid.

## Imported content

The scene model supports mesh control points, polygon triangulation, normals, tangents/binormals, colors, multiple UV layers, material assignments, material values and texture references, model hierarchy/transforms, skin clusters and bone influences, animation curve tracks, and blend-shape position/normal deltas. Cameras and lights are intentionally not imported.

Binary arrays support uncompressed and zlib-compressed payloads. ASCII numeric arrays are parsed into typed numeric storage. Parser resource limits are configurable through FBXParserOptions.

## Notes

- Textures are represented by their source paths and any embedded Video.Content bytes; the importer does not resolve external files.
- Meshes are emitted as triangle lists. Concave polygons use ear clipping; degenerate/self-intersecting polygons fall back to a deterministic fan triangulation.
- Skinned mesh influences are stored in a flat array with per-vertex offsets: vertex `i` reads `BoneInfluences[BoneInfluenceOffsets[i]..BoneInfluenceOffsets[i + 1]]`. This avoids one separate managed array per vertex.
- Mesh streams are allocated at their validated final sizes. Triangulation and generated normal/tangent scratch buffers use `ArrayPool<T>` and are returned after each mesh; meshes with complete imported normals/tangents skip the corresponding scratch buffers.
- Animation curves are sampled at their key times and currently represented as linear key tracks. The source key data remains available in the generic document tree for later interpolation-mode support.
- .AuraAsset serialization, cameras, lights, and FBX export are outside this module's scope.
