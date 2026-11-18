using System.Buffers;
using System.Globalization;
using Auralis.Core.Mathematics;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace Auralis.Tools.AssetPipeline.Interchange.FBX;

/// <summary>
/// Converts the generic FBX document tree into an in-memory scene representation suitable
/// for downstream Auralis asset processing. No engine runtime types or native libraries are required.
/// </summary>
public static class FBXImporter
{
    private const double FbxTicksPerSecond = 46_186_158_000d;

    private readonly record struct Connection(string Type, long Source, long Destination, string? Property);

    public static FBXImportedScene Import(FBXDocument document, FBXImporterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new FBXImporterOptions();
        if (options.MaxImportedVertices <= 0 || options.MaxImportedPolygons <= 0 || options.MaxPolygonVertices < 3)
            throw new ArgumentOutOfRangeException(nameof(options), "Import limits must be positive.");

        var scene = new FBXImportedScene();
        FBXNode? objectsNode = document.GetRootNode("Objects");
        if (objectsNode is null)
        {
            scene.Diagnostics = [new FBXImportDiagnostic(FBXDiagnosticSeverity.Warning, "The FBX document contains no Objects section.")];
            return scene;
        }

        List<FBXNode> objects = objectsNode.Children.ToList();
        var objectById = new Dictionary<long, FBXNode>(objects.Count);
        foreach (FBXNode obj in objects)
        {
            long id = obj.GetInt64(0, long.MinValue);
            if (id == long.MinValue) continue;
            if (!objectById.TryAdd(id, obj))
                throw new FBXFormatException($"Duplicate FBX object id {id}.");
        }

        List<Connection> connections = ReadConnections(document.GetRootNode("Connections"));
        var diagnostics = new List<FBXImportDiagnostic>();
        var coordinates = CoordinateSystem.Read(document.GetRootNode("GlobalSettings"), diagnostics);
        scene.Name = GetObjectName(ReadNodeString(document.GetRootNode("DocumentInfo")?.GetChild("Title")), "Scene");

        var textures = ImportTextures(objects, connections, objectById, options);
        scene.Textures = textures;
        var textureIndices = textures.Select((texture, index) => (texture.SourceId, index))
            .ToDictionary(static pair => pair.SourceId, static pair => pair.index);

        var materials = options.ImportMaterials
            ? ImportMaterials(objects, connections, textureIndices)
            : new List<FBXImportedMaterial>();
        scene.Materials = materials;
        var materialIndices = materials.Select((material, index) => (material.SourceId, index))
            .ToDictionary(static pair => pair.SourceId, static pair => pair.index);

        List<FBXNode> modelObjects = objects.Where(static node => node.Name == "Model").ToList();
        var nodeIndexById = new Dictionary<long, int>(modelObjects.Count);
        for (int i = 0; i < modelObjects.Count; i++)
            nodeIndexById[modelObjects[i].GetInt64(0)] = i;

        var importedNodes = new FBXImportedNode[modelObjects.Count];
        var childLists = new List<int>[modelObjects.Count];
        for (int i = 0; i < modelObjects.Count; i++)
        {
            FBXNode model = modelObjects[i];
            childLists[i] = [];
            Matrix4x4 local = BuildLocalTransform(model, coordinates);
            Matrix engineLocal = local;
            bool decomposed = engineLocal.Decompose(out Vector3D scale, out Quaternion rotation, out Vector3D translation);
            if (!decomposed)
            {
                scale = Vector3D.One;
                rotation = Quaternion.Identity;
                translation = new Vector3D(local.M41, local.M42, local.M43);
            }
            importedNodes[i] = new FBXImportedNode
            {
                SourceId = model.GetInt64(0),
                Name = GetObjectName(model.GetString(1), $"Node_{i}"),
                AttributeType = model.GetString(2) ?? string.Empty,
                LocalTransform = engineLocal,
                GeometryTransform = BuildGeometryTransform(model, coordinates),
                Translation = translation,
                Rotation = rotation.LengthSquared() > 1e-12f ? Quaternion.Normalize(rotation) : Quaternion.Identity,
                Scale = scale
            };
        }

        foreach (Connection connection in connections)
        {
            if (connection.Type != "OO" ||
                !nodeIndexById.TryGetValue(connection.Source, out int childIndex) ||
                !nodeIndexById.TryGetValue(connection.Destination, out int parentIndex))
                continue;
            if (childIndex == parentIndex)
            {
                diagnostics.Add(new FBXImportDiagnostic(FBXDiagnosticSeverity.Warning, "Ignored a self-parenting model connection.", connection.Source));
                continue;
            }
            if (importedNodes[childIndex].ParentIndex >= 0)
            {
                diagnostics.Add(new FBXImportDiagnostic(FBXDiagnosticSeverity.Warning, "A model has multiple parents; the first parent connection was retained.", connection.Source));
                continue;
            }
            importedNodes[childIndex].ParentIndex = parentIndex;
            childLists[parentIndex].Add(childIndex);
        }

        for (int i = 0; i < importedNodes.Length; i++)
            importedNodes[i].Children = childLists[i].ToArray();

        var geometries = objects.Where(static node =>
            node.Name == "Geometry" && string.Equals(node.GetString(2), "Mesh", StringComparison.OrdinalIgnoreCase)).ToList();
        var meshIndexByGeometryId = new Dictionary<long, int>(geometries.Count);
        var meshes = new List<FBXImportedMesh>(geometries.Count);

        foreach (FBXNode geometry in geometries)
        {
            FBXImportedMesh mesh = ImportMesh(geometry, coordinates, options, diagnostics);
            meshIndexByGeometryId[mesh.SourceId] = meshes.Count;
            meshes.Add(mesh);
        }

        var modelMaterials = new Dictionary<long, List<int>>();
        var modelMeshes = new Dictionary<long, List<int>>();
        foreach (Connection connection in connections)
        {
            if (connection.Type == "OO" &&
                materialIndices.TryGetValue(connection.Source, out int materialIndex) &&
                nodeIndexById.ContainsKey(connection.Destination))
            {
                if (!modelMaterials.TryGetValue(connection.Destination, out List<int>? list))
                    modelMaterials[connection.Destination] = list = [];
                list.Add(materialIndex);
            }

            if (connection.Type == "OO" &&
                meshIndexByGeometryId.TryGetValue(connection.Source, out int meshIndex) &&
                nodeIndexById.ContainsKey(connection.Destination))
            {
                if (!modelMeshes.TryGetValue(connection.Destination, out List<int>? meshesForModel))
                    modelMeshes[connection.Destination] = meshesForModel = [];
                meshesForModel.Add(meshIndex);
            }
        }

        for (int i = 0; i < importedNodes.Length; i++)
        {
            if (modelMaterials.TryGetValue(importedNodes[i].SourceId, out List<int>? list))
                importedNodes[i].MaterialIndices = list.Distinct().ToArray();
            if (modelMeshes.TryGetValue(importedNodes[i].SourceId, out List<int>? meshesForModel))
            {
                importedNodes[i].MeshIndices = meshesForModel.Distinct().ToArray();
                importedNodes[i].MeshIndex = importedNodes[i].MeshIndices.Count > 0
                    ? importedNodes[i].MeshIndices[0]
                    : -1;
            }
        }

        var skeletons = new List<FBXImportedSkeleton>();
        if (options.ImportSkinning)
        {
            for (int i = 0; i < meshes.Count; i++)
            {
                FBXImportedSkeleton? skeleton = ImportSkin(
                    meshes[i], connections, objectById, nodeIndexById,
                    importedNodes, coordinates, diagnostics);
                if (skeleton is not null)
                    skeletons.Add(skeleton);
            }
        }

        if (options.ImportBlendShapes)
        {
            foreach (FBXImportedMesh mesh in meshes)
                mesh.BlendShapes = ImportBlendShapes(mesh, objects, objectById, connections, coordinates);
        }

        // Source control points are needed only while calculating absolute morph-target deltas.
        foreach (FBXImportedMesh mesh in meshes)
            mesh.SourceControlPoints = Array.Empty<Vector3D>();

        if (options.ImportAnimation)
            scene.Animations = ImportAnimations(objects, objectById, connections, nodeIndexById, modelObjects, importedNodes, meshes, coordinates);

        ComputeWorldTransforms(importedNodes, diagnostics);

        scene.Nodes = importedNodes;
        scene.Meshes = meshes;
        scene.Skeletons = skeletons;
        scene.Diagnostics = diagnostics;
        return scene;
    }

    private static List<Connection> ReadConnections(FBXNode? connectionsNode)
    {
        var connections = new List<Connection>();
        if (connectionsNode is null) return connections;
        foreach (FBXNode node in connectionsNode.GetChildren("C"))
        {
            string? type = node.GetString(0);
            if (type is null || node.Properties.Count < 3) continue;
            connections.Add(new Connection(type, node.GetInt64(1), node.GetInt64(2), node.GetString(3)));
        }
        return connections;
    }

    private static List<FBXImportedTexture> ImportTextures(
        List<FBXNode> objects, List<Connection> connections,
        Dictionary<long, FBXNode> objectById, FBXImporterOptions options)
    {
        if (!options.ImportTextures) return [];
        var result = new List<FBXImportedTexture>();
        foreach (FBXNode texture in objects.Where(static node => node.Name == "Texture"))
        {
            long id = texture.GetInt64(0);
            string fileName = ReadNodeString(texture.GetChild("FileName")) ??
                              ReadNodeString(texture.GetChild("Filename")) ?? string.Empty;
            string relative = ReadNodeString(texture.GetChild("RelativeFilename")) ??
                              ReadNodeString(texture.GetChild("RelativeFileName")) ?? string.Empty;

            byte[]? embedded = null;
            foreach (Connection connection in connections)
            {
                if (connection.Destination != id || !objectById.TryGetValue(connection.Source, out FBXNode? video) || video.Name != "Video")
                    continue;
                Array? content = video.GetArray("Content");
                if (content is byte[] bytes)
                    embedded = bytes;
                else if (content is not null)
                    embedded = ConvertEmbeddedContent(content);
                if (fileName.Length == 0)
                    fileName = ReadNodeString(video.GetChild("Filename")) ?? string.Empty;
                if (relative.Length == 0)
                    relative = ReadNodeString(video.GetChild("RelativeFilename")) ?? string.Empty;
                break;
            }

            result.Add(new FBXImportedTexture
            {
                SourceId = id,
                Name = GetObjectName(texture.GetString(1), $"Texture_{id}"),
                FileName = fileName,
                RelativeFileName = relative,
                EmbeddedContent = embedded
            });
        }
        return result;
    }

    private static byte[] ConvertEmbeddedContent(Array content)
    {
        var bytes = new byte[content.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            object? value = content.GetValue(i);
            try
            {
                int numeric = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                if ((uint)numeric > byte.MaxValue)
                    throw new FBXFormatException("Embedded FBX video content contains a value outside the byte range.");
                bytes[i] = (byte)numeric;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                throw new FBXFormatException("Embedded FBX video content is not a byte array.", innerException: ex);
            }
        }
        return bytes;
    }

    private static List<FBXImportedMaterial> ImportMaterials(
        List<FBXNode> objects, List<Connection> connections, Dictionary<long, int> textureIndices)
    {
        var result = new List<FBXImportedMaterial>();
        foreach (FBXNode material in objects.Where(static node => node.Name == "Material"))
        {
            FBXNode? props = material.GetChild("Properties70") ?? material.GetChild("Properties60");
            Vector3D diffuse = ReadVectorProperty(props, "DiffuseColor",
                ReadVectorProperty(props, "BaseColor", Vector3D.One));
            Vector3D specular = ReadVectorProperty(props, "SpecularColor", new Vector3D(0.04f));
            Vector3D emissive = ReadVectorProperty(props, "EmissiveColor",
                ReadVectorProperty(props, "Emissive", Vector3D.Zero));
            diffuse *= ReadFloatProperty(props, "DiffuseFactor", 1f);
            specular *= ReadFloatProperty(props, "SpecularFactor", 1f);
            emissive *= ReadFloatProperty(props, "EmissiveFactor", 1f);
            float opacity = ReadFloatProperty(props, "Opacity", 1f);
            if (TryReadProperty(props, "TransparencyFactor", out double transparency))
                opacity *= (float)Math.Clamp(1d - transparency, 0d, 1d);
            float shininess = ReadFloatProperty(props, "Shininess", 32f);
            float roughness = ReadFloatProperty(props, "Roughness", MathF.Sqrt(2f / MathF.Max(2f, shininess + 2f)));
            var imported = new FBXImportedMaterial
            {
                SourceId = material.GetInt64(0),
                Name = GetObjectName(material.GetString(1), $"Material_{material.GetInt64(0)}"),
                BaseColor = diffuse,
                SpecularColor = specular,
                EmissiveColor = emissive,
                Metallic = Math.Clamp(ReadFloatProperty(props, "Metalness",
                    ReadFloatProperty(props, "Metallic", 0f)), 0f, 1f),
                Roughness = Math.Clamp(roughness, 0f, 1f),
                Opacity = Math.Clamp(opacity, 0f, 1f)
            };
            result.Add(imported);
        }

        foreach (FBXImportedMaterial material in result)
        {
            var slots = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Connection connection in connections)
            {
                if (connection.Destination == material.SourceId &&
                    connection.Type == "OP" &&
                    textureIndices.TryGetValue(connection.Source, out int textureIndex))
                {
                    slots[connection.Property ?? "Unknown"] = textureIndex;
                }
            }
            material.TextureSlots = slots;
        }
        return result;
    }

    private static FBXImportedMesh ImportMesh(
        FBXNode geometry, CoordinateSystem coordinates, FBXImporterOptions options,
        List<FBXImportDiagnostic> diagnostics)
    {
        float[] sourceVertices = geometry.GetSingleArray("Vertices") ?? [];
        int[] polygonVertexIndices = geometry.GetInt32Array("PolygonVertexIndex") ?? [];
        if (sourceVertices.Length % 3 != 0)
            throw new FBXFormatException($"Mesh '{geometry.GetString(1)}' has a vertex array whose length is not divisible by three.");

        int controlPointCount = sourceVertices.Length / 3;
        if (controlPointCount > options.MaxImportedVertices)
            throw new FBXFormatException($"Mesh '{geometry.GetString(1)}' exceeds the configured vertex limit.");
        if (polygonVertexIndices.Length > options.MaxImportedVertices)
            throw new FBXFormatException($"Mesh '{geometry.GetString(1)}' exceeds the configured imported-vertex limit.");

        // Validate face boundaries and calculate exact output sizes before allocating mesh streams.
        int polygonCount = 0;
        int faceVertexCount = 0;
        long indexCountLong = 0;
        for (int i = 0; i < polygonVertexIndices.Length; i++)
        {
            int encoded = polygonVertexIndices[i];
            int controlPoint = encoded < 0 ? ~encoded : encoded;
            if ((uint)controlPoint >= (uint)controlPointCount)
                throw new FBXFormatException($"Mesh '{geometry.GetString(1)}' references an out-of-range control point.");

            if (++faceVertexCount > options.MaxPolygonVertices)
                throw new FBXFormatException($"Mesh '{geometry.GetString(1)}' contains a polygon that exceeds the configured triangulation limit.");

            if (encoded < 0)
            {
                if (faceVertexCount < 3)
                    throw new FBXFormatException($"Mesh '{geometry.GetString(1)}' contains a polygon with fewer than three vertices.");
                if (++polygonCount > options.MaxImportedPolygons)
                    throw new FBXFormatException($"Mesh '{geometry.GetString(1)}' exceeds the configured polygon limit.");
                indexCountLong += (long)(faceVertexCount - 2) * 3;
                if (indexCountLong > int.MaxValue)
                    throw new FBXFormatException($"Mesh '{geometry.GetString(1)}' has too many triangulated indices.");
                faceVertexCount = 0;
            }
        }
        if (faceVertexCount != 0)
            throw new FBXFormatException($"Mesh '{geometry.GetString(1)}' has a polygon that is not terminated by a negative index.");

        var controlPoints = new Vector3D[controlPointCount];
        for (int i = 0; i < controlPointCount; i++)
        {
            int offset = i * 3;
            float x = sourceVertices[offset];
            float y = sourceVertices[offset + 1];
            float z = sourceVertices[offset + 2];
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                throw new FBXFormatException($"Mesh '{geometry.GetString(1)}' contains a non-finite control point.");

            Vector3D converted = coordinates.Position(new Vector3D(x, y, z));
            if (!IsFinite(converted))
                throw new FBXFormatException($"Mesh '{geometry.GetString(1)}' overflows while converting its coordinate system.");
            controlPoints[i] = converted;
        }

        LayerData? normals = LayerData.Create(geometry.GetChild("LayerElementNormal"), "Normals", "NormalsIndex", 3);
        LayerData? tangents = LayerData.Create(geometry.GetChild("LayerElementTangent"), "Tangents", "TangentsIndex", 3);
        LayerData? binormals = LayerData.Create(geometry.GetChild("LayerElementBinormal"), "Binormals", "BinormalsIndex", 3);
        LayerData? colors = LayerData.Create(geometry.GetChild("LayerElementColor"), "Colors", "ColorIndex", 4);
        LayerData? materials = LayerData.Create(geometry.GetChild("LayerElementMaterial"), "Materials", null, 1);

        var uvLayers = new List<LayerData>(4);
        foreach (FBXNode uvNode in geometry.GetChildren("LayerElementUV"))
        {
            LayerData? uvLayer = LayerData.Create(uvNode, "UV", "UVIndex", 2);
            if (uvLayer is not null)
                uvLayers.Add(uvLayer);
        }

        int importedVertexCount = polygonVertexIndices.Length;
        int importedIndexCount = (int)indexCountLong;
        var positions = new Vector3D[importedVertexCount];
        var outputNormals = new Vector3D[importedVertexCount];
        var outputTangents = new Vector4D[importedVertexCount];
        var outputColors = new Vector4D[importedVertexCount];
        var sourceControlPointIndices = new int[importedVertexCount];
        var outputUvs = new Vector2D[uvLayers.Count][];
        for (int i = 0; i < outputUvs.Length; i++)
            outputUvs[i] = new Vector2D[importedVertexCount];

        var indices = new int[importedIndexCount];
        var submeshBuffer = new FBXImportedSubmesh[polygonCount];

        // Scratch storage is reused for every polygon in this mesh; the per-polygon
        // triangulator does not allocate Lists, arrays, or iterator objects.
        int scratchCapacity = Math.Min(options.MaxPolygonVertices, Math.Max(3, polygonVertexIndices.Length));
        Corner[] rentedCorners = ArrayPool<Corner>.Shared.Rent(scratchCapacity);
        Vector2D[] rentedProjected = ArrayPool<Vector2D>.Shared.Rent(scratchCapacity);
        int[] rentedRemaining = ArrayPool<int>.Shared.Rent(scratchCapacity);

        int outputVertex = 0;
        int outputIndex = 0;
        int submeshCount = 0;
        int polygonOrdinal = 0;
        int polygonLength = 0;
        int globalPolygonVertex = 0;
        int currentMaterial = int.MinValue;
        int currentSubmeshStart = 0;

        try
        {
            for (int i = 0; i < polygonVertexIndices.Length; i++)
            {
                int encoded = polygonVertexIndices[i];
                int controlPoint = encoded < 0 ? ~encoded : encoded;
                rentedCorners[polygonLength++] = new Corner(controlPoint, globalPolygonVertex++);

                if (encoded >= 0)
                    continue;

                int materialIndex = materials?.ResolveInt(
                    rentedCorners[0].ControlPoint,
                    rentedCorners[0].PolygonVertex,
                    polygonOrdinal) ?? 0;

                if (materialIndex != currentMaterial)
                {
                    if (currentMaterial != int.MinValue)
                    {
                        submeshBuffer[submeshCount++] = new FBXImportedSubmesh(
                            currentSubmeshStart, outputIndex - currentSubmeshStart, currentMaterial);
                    }
                    currentMaterial = materialIndex;
                    currentSubmeshStart = outputIndex;
                }

                int vertexBase = outputVertex;
                for (int cornerIndex = 0; cornerIndex < polygonLength; cornerIndex++)
                {
                    Corner corner = rentedCorners[cornerIndex];
                    positions[outputVertex] = controlPoints[corner.ControlPoint];
                    sourceControlPointIndices[outputVertex] = corner.ControlPoint;

                    Vector3D normal = Vector3D.Zero;
                    if (normals is not null &&
                        normals.TryResolve(corner.ControlPoint, corner.PolygonVertex, polygonOrdinal, out Vector4D normalValue))
                    {
                        normal = coordinates.Direction(new Vector3D(normalValue.X, normalValue.Y, normalValue.Z));
                    }
                    outputNormals[outputVertex] = NormalizeOrZero(normal);

                    Vector4D tangent = Vector4D.Zero;
                    if (tangents is not null &&
                        tangents.TryResolve(corner.ControlPoint, corner.PolygonVertex, polygonOrdinal, out Vector4D tangentValue))
                    {
                        Vector3D tangentDirection = coordinates.Direction(
                            new Vector3D(tangentValue.X, tangentValue.Y, tangentValue.Z));
                        float sign = 1f;

                        if (binormals is not null &&
                            binormals.TryResolve(corner.ControlPoint, corner.PolygonVertex, polygonOrdinal, out Vector4D binormalValue))
                        {
                            Vector3D binormal = coordinates.Direction(
                                new Vector3D(binormalValue.X, binormalValue.Y, binormalValue.Z));
                            if (normal.LengthSquared() > MathUtilities.ZeroTolerance * MathUtilities.ZeroTolerance &&
                                Vector3D.Dot(Vector3D.Cross(normal, tangentDirection), binormal) < 0f)
                            {
                                sign = -1f;
                            }
                        }

                        Vector3D normalizedTangent = NormalizeOrZero(tangentDirection);
                        tangent = new Vector4D(normalizedTangent.X, normalizedTangent.Y, normalizedTangent.Z, sign);
                    }
                    outputTangents[outputVertex] = tangent;

                    if (colors is not null &&
                        colors.TryResolve(corner.ControlPoint, corner.PolygonVertex, polygonOrdinal, out Vector4D color))
                    {
                        outputColors[outputVertex] = color;
                    }
                    else
                    {
                        outputColors[outputVertex] = Vector4D.One;
                    }

                    for (int uvIndex = 0; uvIndex < uvLayers.Count; uvIndex++)
                    {
                        if (uvLayers[uvIndex].TryResolve(
                            corner.ControlPoint, corner.PolygonVertex, polygonOrdinal, out Vector4D uvValue))
                        {
                            outputUvs[uvIndex][outputVertex] = new Vector2D(uvValue.X, uvValue.Y);
                        }
                        else
                        {
                            outputUvs[uvIndex][outputVertex] = Vector2D.Zero;
                        }
                    }

                    outputVertex++;
                }

                int faceIndexCount = (polygonLength - 2) * 3;
                Span<int> faceIndices = indices.AsSpan(outputIndex, faceIndexCount);
                int written = Triangulate(
                    rentedCorners.AsSpan(0, polygonLength),
                    controlPoints,
                    rentedProjected.AsSpan(0, polygonLength),
                    rentedRemaining.AsSpan(0, polygonLength),
                    faceIndices);

                if (written != faceIndexCount)
                    throw new FBXFormatException($"Mesh '{geometry.GetString(1)}' triangulation produced an invalid index count.");

                for (int triangle = 0; triangle < written; triangle += 3)
                {
                    int a = vertexBase + faceIndices[triangle];
                    int b = vertexBase + faceIndices[triangle + 1];
                    int c = vertexBase + faceIndices[triangle + 2];
                    faceIndices[triangle] = a;
                    faceIndices[triangle + 1] = coordinates.FlipsWinding ? c : b;
                    faceIndices[triangle + 2] = coordinates.FlipsWinding ? b : c;
                }

                outputIndex += written;
                polygonOrdinal++;
                polygonLength = 0;
            }

            if (currentMaterial != int.MinValue)
            {
                submeshBuffer[submeshCount++] = new FBXImportedSubmesh(
                    currentSubmeshStart, outputIndex - currentSubmeshStart, currentMaterial);
            }
        }
        finally
        {
            ArrayPool<Corner>.Shared.Return(rentedCorners, clearArray: false);
            ArrayPool<Vector2D>.Shared.Return(rentedProjected, clearArray: false);
            ArrayPool<int>.Shared.Return(rentedRemaining, clearArray: false);
        }

        if (outputVertex != importedVertexCount || outputIndex != importedIndexCount)
            throw new FBXFormatException($"Mesh '{geometry.GetString(1)}' produced an inconsistent output stream.");

        if (options.GenerateMissingNormals)
            GenerateNormals(positions, indices, sourceControlPointIndices, controlPointCount, outputNormals);
        if (options.GenerateMissingTangents)
            GenerateTangents(positions, outputNormals, indices,
                outputUvs.Length > 0 ? outputUvs[0] : null, outputTangents);

        if (normals is null)
        {
            diagnostics.Add(new FBXImportDiagnostic(
                FBXDiagnosticSeverity.Info,
                $"Generated normals for mesh '{geometry.GetString(1)}'.",
                geometry.GetInt64(0)));
        }

        if (submeshCount != submeshBuffer.Length)
            Array.Resize(ref submeshBuffer, submeshCount);

        return new FBXImportedMesh
        {
            SourceId = geometry.GetInt64(0),
            Name = GetObjectName(geometry.GetString(1), $"Mesh_{geometry.GetInt64(0)}"),
            Positions = positions,
            Normals = outputNormals,
            Tangents = outputTangents,
            TextureCoordinates = outputUvs,
            Colors = outputColors,
            Indices = indices,
            SourceControlPointIndices = sourceControlPointIndices,
            ControlPointCount = controlPointCount,
            SourceControlPoints = controlPoints,
            Submeshes = submeshBuffer
        };
    }

    private readonly record struct Corner(int ControlPoint, int PolygonVertex);

    private sealed class LayerData
    {
        private readonly float[] _values;
        private readonly int[]? _indices;
        private readonly int _components;
        private readonly string _mapping;
        private readonly bool _indexed;

        private LayerData(float[] values, int[]? indices, int components, string mapping, bool indexed)
        {
            _values = values;
            _indices = indices;
            _components = components;
            _mapping = mapping;
            _indexed = indexed;
        }

        public static LayerData? Create(FBXNode? layer, string dataName, string? indexName, int components)
        {
            if (layer is null)
                return null;

            float[]? values = layer.GetSingleArray(dataName);
            if (values is null || values.Length == 0)
                return null;

            int[]? indices = indexName is null ? null : layer.GetInt32Array(indexName);
            string mapping = ReadNodeString(layer.GetChild("MappingInformationType")) ?? "ByPolygonVertex";
            string reference = ReadNodeString(layer.GetChild("ReferenceInformationType")) ?? "Direct";
            // Material layers contain direct per-polygon integer indices and do not
            // have a separate MaterialsIndex array.
            bool indexed = indexName is not null && (reference is "IndexToDirect" or "Index");
            return new LayerData(values, indices, components, mapping, indexed);
        }

        public bool TryResolve(int controlPoint, int polygonVertex, int polygon, out Vector4D value)
        {
            value = Vector4D.Zero;
            int sample = _mapping switch
            {
                "ByVertice" or "ByVertex" => controlPoint,
                "ByPolygon" => polygon,
                "AllSame" => 0,
                _ => polygonVertex
            };
            if (sample < 0)
                return false;

            int direct = sample;
            if (_indexed)
            {
                if (_indices is null || (uint)sample >= (uint)_indices.Length)
                    return false;
                direct = _indices[sample];
            }

            long offsetLong = (long)direct * _components;
            if (direct < 0 || offsetLong < 0 || offsetLong > _values.Length - _components)
                return false;

            int offset = (int)offsetLong;
            value = new Vector4D(
                _values[offset],
                _components > 1 ? _values[offset + 1] : 0f,
                _components > 2 ? _values[offset + 2] : 0f,
                _components > 3 ? _values[offset + 3] : 1f);
            return true;
        }

        public int ResolveInt(int controlPoint, int polygonVertex, int polygon)
        {
            return TryResolve(controlPoint, polygonVertex, polygon, out Vector4D value)
                ? (int)value.X
                : 0;
        }
    }

    /// <summary>
    /// Ear-clips a polygon into a triangle list using caller-provided scratch spans.
    /// Returns exactly (polygon.Length - 2) * 3 local corner indices.
    /// </summary>
    private static int Triangulate(
        ReadOnlySpan<Corner> polygon,
        ReadOnlySpan<Vector3D> positions,
        Span<Vector2D> projected,
        Span<int> remaining,
        Span<int> destination)
    {
        int count = polygon.Length;
        int required = (count - 2) * 3;
        if (count == 3)
        {
            destination[0] = 0;
            destination[1] = 1;
            destination[2] = 2;
            return 3;
        }

        Vector3D normal = Vector3D.Zero;
        for (int i = 0; i < count; i++)
        {
            Vector3D current = positions[polygon[i].ControlPoint];
            Vector3D next = positions[polygon[(i + 1) % count].ControlPoint];
            normal.X += (current.Y - next.Y) * (current.Z + next.Z);
            normal.Y += (current.Z - next.Z) * (current.X + next.X);
            normal.Z += (current.X - next.X) * (current.Y + next.Y);
        }

        int dropAxis = MathF.Abs(normal.X) > MathF.Abs(normal.Y)
            ? (MathF.Abs(normal.X) > MathF.Abs(normal.Z) ? 0 : 2)
            : (MathF.Abs(normal.Y) > MathF.Abs(normal.Z) ? 1 : 2);

        for (int i = 0; i < count; i++)
        {
            Vector3D point = positions[polygon[i].ControlPoint];
            projected[i] = dropAxis switch
            {
                0 => new Vector2D(point.Y, point.Z),
                1 => new Vector2D(point.X, point.Z),
                _ => new Vector2D(point.X, point.Y)
            };
        }

        double area = 0d;
        for (int i = 0; i < count; i++)
        {
            Vector2D a = projected[i];
            Vector2D b = projected[(i + 1) % count];
            area += (double)a.X * b.Y - (double)b.X * a.Y;
        }

        // Keep the triangulation degeneracy threshold aligned with the previous
        // double-precision implementation; ZeroToleranceDouble currently represents
        // double.Epsilon * 8 rather than machine epsilon.
        const double areaTolerance = 1e-12d;
        if (Math.Abs(area) < areaTolerance)
            return FanTriangulate(count, destination);

        float orientation = area > 0d ? 1f : -1f;
        for (int i = 0; i < count; i++)
            remaining[i] = i;

        int remainingCount = count;
        int written = 0;
        int attempts = 0;
        int maxAttempts = count * count;

        while (remainingCount > 3 && attempts < maxAttempts)
        {
            bool clipped = false;
            for (int i = 0; i < remainingCount; i++)
            {
                int previous = remaining[(i + remainingCount - 1) % remainingCount];
                int current = remaining[i];
                int next = remaining[(i + 1) % remainingCount];

                Vector2D edge1 = projected[current] - projected[previous];
                Vector2D edge2 = projected[next] - projected[current];
                if (Cross2(edge1, edge2) * orientation <= MathUtilities.ZeroTolerance)
                    continue;

                bool contains = false;
                for (int j = 0; j < remainingCount; j++)
                {
                    int test = remaining[j];
                    if (test == previous || test == current || test == next)
                        continue;
                    if (PointInTriangle(projected[test], projected[previous],
                        projected[current], projected[next], orientation))
                    {
                        contains = true;
                        break;
                    }
                }

                if (contains)
                    continue;

                destination[written++] = previous;
                destination[written++] = current;
                destination[written++] = next;

                remaining.Slice(i + 1, remainingCount - i - 1)
                    .CopyTo(remaining.Slice(i, remainingCount - i - 1));
                remainingCount--;
                clipped = true;
                break;
            }

            if (!clipped)
                break;
            attempts++;
        }

        if (remainingCount == 3)
        {
            destination[written++] = remaining[0];
            destination[written++] = remaining[1];
            destination[written++] = remaining[2];
            return written;
        }

        // Degenerate or self-intersecting polygons retain the importer’s deterministic fan fallback.
        return FanTriangulate(count, destination);
    }

    private static int FanTriangulate(int count, Span<int> destination)
    {
        int cursor = 0;
        for (int i = 1; i < count - 1; i++)
        {
            destination[cursor++] = 0;
            destination[cursor++] = i;
            destination[cursor++] = i + 1;
        }
        return cursor;
    }

    private static float Cross2(Vector2D a, Vector2D b) => a.X * b.Y - a.Y * b.X;

    private static bool PointInTriangle(
        Vector2D point, Vector2D a, Vector2D b, Vector2D c, float orientation)
    {
        float ab = Cross2(b - a, point - a) * orientation;
        float bc = Cross2(c - b, point - b) * orientation;
        float ca = Cross2(a - c, point - c) * orientation;
        return ab >= -MathUtilities.ZeroTolerance &&
               bc >= -MathUtilities.ZeroTolerance &&
               ca >= -MathUtilities.ZeroTolerance;
    }

    private static void GenerateNormals(
        Vector3D[] positions, int[] indices, int[] sourceControlPoints,
        int controlPointCount, Vector3D[] normals)
    {
        // Avoid even renting scratch memory when all source normals are present.
        float zeroLengthSquared = MathUtilities.ZeroTolerance * MathUtilities.ZeroTolerance;
        bool hasMissingNormals = false;
        for (int i = 0; i < normals.Length; i++)
        {
            if (normals[i].LengthSquared() < zeroLengthSquared)
            {
                hasMissingNormals = true;
                break;
            }
        }
        if (!hasMissingNormals)
            return;

        // Imported vertices are split at polygon corners to preserve attribute seams.
        // Accumulate by source control point in a pooled buffer.
        Vector3D[] rented = ArrayPool<Vector3D>.Shared.Rent(Math.Max(1, controlPointCount));
        try
        {
            Span<Vector3D> accumulated = rented.AsSpan(0, controlPointCount);
            accumulated.Clear();

            for (int i = 0; i < indices.Length; i += 3)
            {
                int a = indices[i];
                int b = indices[i + 1];
                int c = indices[i + 2];
                Vector3D faceNormal = Vector3D.Cross(
                    positions[b] - positions[a],
                    positions[c] - positions[a]);

                accumulated[sourceControlPoints[a]] += faceNormal;
                accumulated[sourceControlPoints[b]] += faceNormal;
                accumulated[sourceControlPoints[c]] += faceNormal;
            }

            for (int i = 0; i < normals.Length; i++)
            {
                if (normals[i].LengthSquared() < zeroLengthSquared)
                    normals[i] = NormalizeOrZero(accumulated[sourceControlPoints[i]]);
            }
        }
        finally
        {
            ArrayPool<Vector3D>.Shared.Return(rented, clearArray: false);
        }
    }

    private static void GenerateTangents(
        Vector3D[] positions, Vector3D[] normals, int[] indices,
        Vector2D[]? uvs, Vector4D[] tangents)
    {
        if (uvs is null || uvs.Length != positions.Length)
            return;

        // Most meshes arrive with usable tangents; don't rent two full vertex-sized
        // buffers just to discover that no tangent generation is necessary.
        float zeroLengthSquared = MathUtilities.ZeroTolerance * MathUtilities.ZeroTolerance;
        bool hasMissingTangents = false;
        for (int i = 0; i < tangents.Length; i++)
        {
            Vector3D existing = new(tangents[i].X, tangents[i].Y, tangents[i].Z);
            if (existing.LengthSquared() <= zeroLengthSquared)
            {
                hasMissingTangents = true;
                break;
            }
        }
        if (!hasMissingTangents)
            return;

        Vector3D[] rentedTangents = ArrayPool<Vector3D>.Shared.Rent(Math.Max(1, positions.Length));
        Vector3D[] rentedBitangents = ArrayPool<Vector3D>.Shared.Rent(Math.Max(1, positions.Length));
        try
        {
            Span<Vector3D> tangentSums = rentedTangents.AsSpan(0, positions.Length);
            Span<Vector3D> bitangentSums = rentedBitangents.AsSpan(0, positions.Length);
            tangentSums.Clear();
            bitangentSums.Clear();

            float determinantTolerance = MathUtilities.ZeroTolerance * MathUtilities.ZeroTolerance;
            for (int i = 0; i < indices.Length; i += 3)
            {
                int i0 = indices[i];
                int i1 = indices[i + 1];
                int i2 = indices[i + 2];

                Vector3D edge1 = positions[i1] - positions[i0];
                Vector3D edge2 = positions[i2] - positions[i0];
                Vector2D delta1 = uvs[i1] - uvs[i0];
                Vector2D delta2 = uvs[i2] - uvs[i0];

                float determinant = delta1.X * delta2.Y - delta1.Y * delta2.X;
                if (MathF.Abs(determinant) < determinantTolerance)
                    continue;

                float inverse = 1f / determinant;
                Vector3D tangent = (edge1 * delta2.Y - edge2 * delta1.Y) * inverse;
                Vector3D bitangent = (edge2 * delta1.X - edge1 * delta2.X) * inverse;

                tangentSums[i0] += tangent;
                tangentSums[i1] += tangent;
                tangentSums[i2] += tangent;
                bitangentSums[i0] += bitangent;
                bitangentSums[i1] += bitangent;
                bitangentSums[i2] += bitangent;
            }

            for (int i = 0; i < tangents.Length; i++)
            {
                Vector3D existing = new(tangents[i].X, tangents[i].Y, tangents[i].Z);
                if (existing.LengthSquared() > zeroLengthSquared)
                    continue;

                Vector3D normal = normals[i];
                Vector3D tangent = tangentSums[i] -
                    normal * Vector3D.Dot(normal, tangentSums[i]);
                tangent = NormalizeOrZero(tangent);
                float sign = Vector3D.Dot(Vector3D.Cross(normal, tangent), bitangentSums[i]) < 0f ? -1f : 1f;
                tangents[i] = new Vector4D(tangent.X, tangent.Y, tangent.Z, sign);
            }
        }
        finally
        {
            ArrayPool<Vector3D>.Shared.Return(rentedTangents, clearArray: false);
            ArrayPool<Vector3D>.Shared.Return(rentedBitangents, clearArray: false);
        }
    }

    private static FBXImportedSkeleton? ImportSkin(
        FBXImportedMesh mesh, List<Connection> connections,
        Dictionary<long, FBXNode> objectById, Dictionary<long, int> nodeIndexById,
        FBXImportedNode[] importedNodes, CoordinateSystem coordinates,
        List<FBXImportDiagnostic> diagnostics)
    {
        long geometryId = mesh.SourceId;
        var skinIds = new HashSet<long>();
        foreach (Connection connection in connections)
        {
            if (connection.Type == "OO" && connection.Destination == geometryId &&
                objectById.TryGetValue(connection.Source, out FBXNode? deformer) &&
                deformer.Name == "Deformer" &&
                string.Equals(deformer.GetString(2), "Skin", StringComparison.OrdinalIgnoreCase))
            {
                skinIds.Add(connection.Source);
            }
        }
        if (skinIds.Count == 0)
            return null;

        var bones = new List<FBXImportedBone>();
        var influencesByControlPoint = new List<FBXVertexInfluence>?[mesh.ControlPointCount];

        // Walk the connection array in-place instead of materializing a second cluster list.
        foreach (Connection skinConnection in connections)
        {
            if (skinConnection.Type != "OO" || !skinIds.Contains(skinConnection.Destination) ||
                !objectById.TryGetValue(skinConnection.Source, out FBXNode? cluster) ||
                cluster.Name != "Deformer" ||
                !string.Equals(cluster.GetString(2), "Cluster", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // FBX connection direction: Bone Model -> Cluster -> Skin Deformer -> Geometry.
            int nodeIndex = -1;
            for (int i = 0; i < connections.Count; i++)
            {
                Connection boneConnection = connections[i];
                if (boneConnection.Type == "OO" &&
                    boneConnection.Destination == skinConnection.Source &&
                    nodeIndexById.TryGetValue(boneConnection.Source, out nodeIndex))
                {
                    break;
                }
            }
            if (nodeIndex < 0)
                continue;

            int boneIndex = bones.Count;
            Matrix bind = ReadMatrix(cluster.GetDoubleArray("TransformLink"), coordinates);
            if (!Matrix.Invert(bind, out Matrix inverseBind))
            {
                inverseBind = Matrix.Identity;
                diagnostics.Add(new FBXImportDiagnostic(
                    FBXDiagnosticSeverity.Warning,
                    "A skin cluster has a singular TransformLink matrix; identity inverse bind was used.",
                    cluster.GetInt64(0)));
            }

            bones.Add(new FBXImportedBone
            {
                Name = importedNodes[nodeIndex].Name,
                NodeIndex = nodeIndex,
                InverseBindMatrix = inverseBind
            });

            int[] controlPointIndices = cluster.GetInt32Array("Indexes") ?? [];
            double[] weights = cluster.GetDoubleArray("Weights") ?? [];
            int influenceCount = Math.Min(controlPointIndices.Length, weights.Length);
            for (int i = 0; i < influenceCount; i++)
            {
                int controlPoint = controlPointIndices[i];
                float weight = (float)weights[i];
                if ((uint)controlPoint >= (uint)mesh.ControlPointCount ||
                    !float.IsFinite(weight) || weight <= 0f)
                {
                    continue;
                }

                List<FBXVertexInfluence>? list = influencesByControlPoint[controlPoint];
                if (list is null)
                    influencesByControlPoint[controlPoint] = list = new List<FBXVertexInfluence>(4);
                list.Add(new FBXVertexInfluence(boneIndex, weight));
            }
        }

        if (bones.Count == 0)
        {
            diagnostics.Add(new FBXImportDiagnostic(
                FBXDiagnosticSeverity.Warning,
                "A Skin deformer was found but no usable bone clusters were connected.",
                geometryId));
            return null;
        }

        // Build bone-parent indices with a dense lookup rather than a per-bone dictionary.
        int[] boneIndexByNode = new int[importedNodes.Length];
        Array.Fill(boneIndexByNode, -1);
        for (int i = 0; i < bones.Count; i++)
            boneIndexByNode[bones[i].NodeIndex] = i;

        foreach (FBXImportedBone bone in bones)
        {
            int parentNode = importedNodes[bone.NodeIndex].ParentIndex;
            if ((uint)parentNode < (uint)boneIndexByNode.Length)
                bone.ParentBoneIndex = boneIndexByNode[parentNode];
        }

        // A flat stream avoids one small managed array allocation for every vertex.
        int vertexCount = mesh.Positions.Length;
        var offsets = new int[vertexCount + 1];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            List<FBXVertexInfluence>? list =
                influencesByControlPoint[mesh.SourceControlPointIndices[vertex]];
            int count = list?.Count ?? 0;
            try
            {
                offsets[vertex + 1] = checked(offsets[vertex] + count);
            }
            catch (OverflowException ex)
            {
                throw new FBXFormatException("The imported skin influence count exceeds the supported range.", innerException: ex);
            }
        }

        var flatInfluences = new FBXVertexInfluence[offsets[vertexCount]];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            List<FBXVertexInfluence>? list =
                influencesByControlPoint[mesh.SourceControlPointIndices[vertex]];
            if (list is null || list.Count == 0)
                continue;

            double totalWeight = 0d;
            for (int influence = 0; influence < list.Count; influence++)
                totalWeight += list[influence].Weight;

            int destination = offsets[vertex];
            if (totalWeight <= 0d || !double.IsFinite(totalWeight))
                continue;

            for (int influence = 0; influence < list.Count; influence++)
            {
                FBXVertexInfluence source = list[influence];
                flatInfluences[destination + influence] =
                    new FBXVertexInfluence(source.BoneIndex, (float)(source.Weight / totalWeight));
            }
        }

        mesh.BoneInfluences = flatInfluences;
        mesh.BoneInfluenceOffsets = offsets;

        var skeleton = new FBXImportedSkeleton
        {
            Name = mesh.Name + "_Skeleton",
            Bones = bones.ToArray()
        };
        mesh.Skeleton = skeleton;
        return skeleton;
    }

    private static FBXImportedBlendShape[] ImportBlendShapes(
        FBXImportedMesh mesh, List<FBXNode> objects, Dictionary<long, FBXNode> byId,
        List<Connection> connections, CoordinateSystem coordinates)
    {
        var result = new List<FBXImportedBlendShape>();
        var baseNormalSums = new Vector3D[mesh.ControlPointCount];
        var baseNormalCounts = new int[mesh.ControlPointCount];
        for (int i = 0; i < mesh.Normals.Length; i++)
        {
            int controlPoint = mesh.SourceControlPointIndices[i];
            if ((uint)controlPoint >= (uint)baseNormalSums.Length)
                continue;
            baseNormalSums[controlPoint] += mesh.Normals[i];
            baseNormalCounts[controlPoint]++;
        }
        for (int i = 0; i < baseNormalSums.Length; i++)
            if (baseNormalCounts[i] > 0)
                baseNormalSums[i] = NormalizeOrZero(baseNormalSums[i]);
        var blendShapes = objects.Where(static node => node.Name == "Deformer" &&
            string.Equals(node.GetString(2), "BlendShape", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (FBXNode blendShape in blendShapes)
        {
            long blendShapeId = blendShape.GetInt64(0);
            if (!connections.Any(c => c.Type == "OO" && c.Source == blendShapeId && c.Destination == mesh.SourceId))
                continue;

            foreach (Connection channelLink in connections.Where(c => c.Type == "OO" && c.Destination == blendShapeId))
            {
                if (!byId.TryGetValue(channelLink.Source, out FBXNode? channel) ||
                    channel.Name != "Deformer" ||
                    !string.Equals(channel.GetString(2), "BlendShapeChannel", StringComparison.OrdinalIgnoreCase))
                    continue;

                double[] fullWeights = channel.GetDoubleArray("FullWeights") ?? [];
                int shapeOrdinal = 0;
                foreach (Connection shapeLink in connections.Where(c => c.Type == "OO" && c.Destination == channelLink.Source))
                {
                    if (!byId.TryGetValue(shapeLink.Source, out FBXNode? shape) || shape.Name != "Geometry" ||
                        !string.Equals(shape.GetString(2), "Shape", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int[] controlPointIndices = shape.GetInt32Array("Indexes") ?? [];
                    float[] sourceDeltas = shape.GetSingleArray("Vertices") ?? [];
                    float[] sourceNormalDeltas = shape.GetSingleArray("Normals") ?? [];
                    bool absoluteMode = TryReadProperty(shape.GetChild("Properties70"), "AbsoluteMode", out double absoluteValue) &&
                                        absoluteValue != 0d;
                    var deltaMap = new Dictionary<int, Vector3D>(controlPointIndices.Length);
                    var normalMap = new Dictionary<int, Vector3D>(controlPointIndices.Length);
                    int deltaCount = Math.Min(controlPointIndices.Length, sourceDeltas.Length / 3);
                    for (int i = 0; i < deltaCount; i++)
                    {
                        int controlPoint = controlPointIndices[i];
                        if ((uint)controlPoint >= (uint)mesh.ControlPointCount)
                            continue;

                        Vector3D targetPosition = coordinates.Position(new Vector3D(
                            sourceDeltas[i * 3], sourceDeltas[i * 3 + 1], sourceDeltas[i * 3 + 2]));
                        Vector3D positionDelta = absoluteMode
                            ? targetPosition - mesh.SourceControlPoints[controlPoint]
                            : coordinates.Direction(new Vector3D(
                                sourceDeltas[i * 3], sourceDeltas[i * 3 + 1], sourceDeltas[i * 3 + 2])) * coordinates.UnitScale;
                        deltaMap[controlPoint] = positionDelta;

                        if (sourceNormalDeltas.Length >= (i + 1) * 3)
                        {
                            Vector3D targetNormal = coordinates.Direction(new Vector3D(
                                sourceNormalDeltas[i * 3], sourceNormalDeltas[i * 3 + 1], sourceNormalDeltas[i * 3 + 2]));
                            if (absoluteMode && baseNormalCounts[controlPoint] > 0)
                                targetNormal -= baseNormalSums[controlPoint];
                            normalMap[controlPoint] = targetNormal;
                        }
                    }

                    var positionDeltas = new Vector3D[mesh.Positions.Length];
                    var normalDeltas = new Vector3D[mesh.Positions.Length];
                    for (int i = 0; i < positionDeltas.Length; i++)
                    {
                        int cp = mesh.SourceControlPointIndices[i];
                        if (deltaMap.TryGetValue(cp, out Vector3D delta)) positionDeltas[i] = delta;
                        if (normalMap.TryGetValue(cp, out Vector3D normalDelta)) normalDeltas[i] = normalDelta;
                    }

                    result.Add(new FBXImportedBlendShape
                    {
                        SourceId = shape.GetInt64(0),
                        ChannelId = channelLink.Source,
                        Name = GetObjectName(shape.GetString(1), $"BlendShape_{shape.GetInt64(0)}"),
                        FullWeight = shapeOrdinal < fullWeights.Length ? (float)fullWeights[shapeOrdinal] : 100f,
                        PositionDeltas = positionDeltas,
                        NormalDeltas = normalDeltas
                    });
                    shapeOrdinal++;
                }
            }
        }
        return result.ToArray();
    }

    private static IReadOnlyList<FBXImportedAnimationClip> ImportAnimations(
        List<FBXNode> objects, Dictionary<long, FBXNode> objectById, List<Connection> connections,
        Dictionary<long, int> nodeIndexById, List<FBXNode> modelObjects,
        FBXImportedNode[] importedNodes, List<FBXImportedMesh> meshes, CoordinateSystem coordinates)
    {
        var meshByChannelId = new Dictionary<long, int>();
        for (int meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
            foreach (FBXImportedBlendShape shape in meshes[meshIndex].BlendShapes)
                meshByChannelId.TryAdd(shape.ChannelId, meshIndex);
        var clips = new List<FBXImportedAnimationClip>();
        var stacks = objects.Where(static node => node.Name == "AnimationStack").ToList();

        foreach (FBXNode stack in stacks)
        {
            long stackId = stack.GetInt64(0);
            long[] layerIds = connections.Where(c => c.Type == "OO" && c.Destination == stackId &&
                objectById.TryGetValue(c.Source, out FBXNode? source) && source.Name == "AnimationLayer")
                .Select(static c => c.Source).ToArray();
            if (layerIds.Length == 0) continue;

            var tracks = new Dictionary<int, AnimationTrackBuilder>();
            var blendShapeTracks = new List<FBXImportedBlendShapeAnimationTrack>();
            double duration = 0;

            foreach (long layerId in layerIds)
            {
                long[] curveNodeIds = connections.Where(c => c.Type == "OO" && c.Destination == layerId &&
                    objectById.TryGetValue(c.Source, out FBXNode? source) && source.Name == "AnimationCurveNode")
                    .Select(static c => c.Source).ToArray();

                foreach (long curveNodeId in curveNodeIds)
                {
                    Connection modelLink = connections.FirstOrDefault(c => c.Type == "OP" &&
                        c.Source == curveNodeId && nodeIndexById.ContainsKey(c.Destination));
                    Connection blendShapeLink = connections.FirstOrDefault(c => c.Type == "OP" &&
                        c.Source == curveNodeId && meshByChannelId.ContainsKey(c.Destination) &&
                        c.Property == "DeformPercent");

                    if (modelLink.Type is null && blendShapeLink.Type is not null)
                    {
                        var scalarCurves = new Dictionary<char, CurveData>();
                        foreach (Connection curveLink in connections.Where(c => c.Type == "OP" && c.Destination == curveNodeId))
                        {
                            if (curveLink.Property is not ("d|X" or "d|Y" or "d|Z") ||
                                !objectById.TryGetValue(curveLink.Source, out FBXNode? curve) ||
                                curve.Name != "AnimationCurve")
                                continue;
                            char axis = curveLink.Property[^1];
                            scalarCurves[axis] = CurveData.Read(curve);
                        }

                        if (scalarCurves.TryGetValue('X', out CurveData? scalarCurve) && scalarCurve.Times.Length > 0)
                        {
                            FBXScalarKeyframe[] keys = scalarCurve.Times
                                .Select((time, index) => new FBXScalarKeyframe(
                                    time / FbxTicksPerSecond, (float)scalarCurve.Values[index]))
                                .ToArray();
                            blendShapeTracks.Add(new FBXImportedBlendShapeAnimationTrack
                            {
                                MeshIndex = meshByChannelId[blendShapeLink.Destination],
                                ChannelId = blendShapeLink.Destination,
                                Keyframes = keys
                            });
                            duration = Math.Max(duration, keys[^1].TimeSeconds);
                        }
                        continue;
                    }

                    if (modelLink.Type is null) continue;
                    int nodeIndex = nodeIndexById[modelLink.Destination];
                    string property = modelLink.Property ?? string.Empty;
                    if (property is not ("Lcl Translation" or "Lcl Rotation" or "Lcl Scaling"))
                        continue;

                    var axisCurves = new Dictionary<char, CurveData>();
                    foreach (Connection curveLink in connections.Where(c => c.Type == "OP" && c.Destination == curveNodeId))
                    {
                        if (!objectById.TryGetValue(curveLink.Source, out FBXNode? curve) || curve.Name != "AnimationCurve")
                            continue;
                        char axis = curveLink.Property switch
                        {
                            "d|X" => 'X', "d|Y" => 'Y', "d|Z" => 'Z', _ => '\0'
                        };
                        if (axis == '\0') continue;
                        CurveData curveData = CurveData.Read(curve);
                        axisCurves[axis] = curveData;
                        if (curveData.Times.Length > 0)
                            duration = Math.Max(duration, curveData.Times[^1] / FbxTicksPerSecond);
                    }
                    if (axisCurves.Count == 0) continue;

                    SortedSet<long> allTimes = new();
                    foreach (CurveData curve in axisCurves.Values)
                        foreach (long time in curve.Times) allTimes.Add(time);
                    if (!tracks.TryGetValue(nodeIndex, out AnimationTrackBuilder? builder))
                        tracks[nodeIndex] = builder = new AnimationTrackBuilder(nodeIndex);

                    FBXNode model = modelObjects[nodeIndex];
                    if (property == "Lcl Translation")
                    {
                        Vector3D fallback = ReadVectorProperty(model.GetChild("Properties70"), property, Vector3D.Zero);
                        var keys = new List<FBXVectorKeyframe>(allTimes.Count);
                        foreach (long time in allTimes)
                        {
                            Vector3D source = new(
                                (float)Evaluate(axisCurves, 'X', time, fallback.X),
                                (float)Evaluate(axisCurves, 'Y', time, fallback.Y),
                                (float)Evaluate(axisCurves, 'Z', time, fallback.Z));
                            keys.Add(new FBXVectorKeyframe(time / FbxTicksPerSecond, coordinates.Position(source)));
                        }
                        builder.Translation.AddRange(keys);
                    }
                    else if (property == "Lcl Scaling")
                    {
                        Vector3D fallback = ReadVectorProperty(model.GetChild("Properties70"), property, Vector3D.One);
                        var keys = new List<FBXVectorKeyframe>(allTimes.Count);
                        foreach (long time in allTimes)
                        {
                            Vector3D source = new(
                                (float)Evaluate(axisCurves, 'X', time, fallback.X),
                                (float)Evaluate(axisCurves, 'Y', time, fallback.Y),
                                (float)Evaluate(axisCurves, 'Z', time, fallback.Z));
                            keys.Add(new FBXVectorKeyframe(time / FbxTicksPerSecond, coordinates.Scale(source)));
                        }
                        builder.Scale.AddRange(keys);
                    }
                    else
                    {
                        Vector3D fallback = ReadVectorProperty(model.GetChild("Properties70"), property, Vector3D.Zero);
                        var keys = new List<FBXQuaternionKeyframe>(allTimes.Count);
                        int rotationOrder = ReadIntProperty(model.GetChild("Properties70"), "RotationOrder", 0);
                        foreach (long time in allTimes)
                        {
                            Vector3D source = new(
                                (float)Evaluate(axisCurves, 'X', time, fallback.X),
                                (float)Evaluate(axisCurves, 'Y', time, fallback.Y),
                                (float)Evaluate(axisCurves, 'Z', time, fallback.Z));
                            keys.Add(new FBXQuaternionKeyframe(time / FbxTicksPerSecond, coordinates.Rotation(source, rotationOrder)));
                        }
                        builder.Rotation.AddRange(keys);
                    }
                }
            }

            if (tracks.Count == 0 && blendShapeTracks.Count == 0) continue;
            clips.Add(new FBXImportedAnimationClip
            {
                SourceId = stackId,
                Name = GetObjectName(stack.GetString(1), $"Animation_{stackId}"),
                DurationSeconds = duration,
                Tracks = tracks.Values.Select(static builder => builder.Build()).ToArray(),
                BlendShapeTracks = blendShapeTracks
            });
        }
        return clips;
    }

    private sealed class AnimationTrackBuilder
    {
        private readonly int _nodeIndex;
        public List<FBXVectorKeyframe> Translation { get; } = [];
        public List<FBXQuaternionKeyframe> Rotation { get; } = [];
        public List<FBXVectorKeyframe> Scale { get; } = [];
        public AnimationTrackBuilder(int nodeIndex) => _nodeIndex = nodeIndex;
        public FBXImportedAnimationTrack Build() => new()
        {
            NodeIndex = _nodeIndex,
            TranslationKeys = Translation.OrderBy(static key => key.TimeSeconds).DistinctBy(static key => key.TimeSeconds).ToArray(),
            RotationKeys = Rotation.OrderBy(static key => key.TimeSeconds).DistinctBy(static key => key.TimeSeconds).ToArray(),
            ScaleKeys = Scale.OrderBy(static key => key.TimeSeconds).DistinctBy(static key => key.TimeSeconds).ToArray()
        };
    }

    private sealed class CurveData
    {
        public long[] Times { get; private init; } = [];
        public double[] Values { get; private init; } = [];

        public static CurveData Read(FBXNode node)
        {
            long[] times = ToInt64Array(node.GetArray("KeyTime"));
            double[] values = node.GetDoubleArray("KeyValueFloat") ?? [];
            int count = Math.Min(times.Length, values.Length);
            if (count == 0) return new CurveData();

            times = times[..count];
            values = values[..count];
            bool sorted = true;
            for (int i = 1; i < count; i++)
                if (times[i] < times[i - 1])
                {
                    sorted = false;
                    break;
                }

            if (!sorted)
            {
                int[] order = Enumerable.Range(0, count).OrderBy(index => times[index]).ToArray();
                long[] sortedTimes = new long[count];
                double[] sortedValues = new double[count];
                for (int i = 0; i < count; i++)
                {
                    sortedTimes[i] = times[order[i]];
                    sortedValues[i] = values[order[i]];
                }
                times = sortedTimes;
                values = sortedValues;
            }

            // Collapse duplicate timestamps, retaining the last value in file order.
            int write = 0;
            for (int read = 0; read < times.Length; read++)
            {
                if (write > 0 && times[read] == times[write - 1])
                {
                    values[write - 1] = values[read];
                    continue;
                }
                times[write] = times[read];
                values[write] = values[read];
                write++;
            }
            if (write != times.Length)
            {
                Array.Resize(ref times, write);
                Array.Resize(ref values, write);
            }

            return new CurveData { Times = times, Values = values };
        }

        public double Evaluate(long time, double fallback)
        {
            if (Times.Length == 0) return fallback;
            if (time <= Times[0]) return Values[0];
            if (time >= Times[^1]) return Values[^1];
            int index = Array.BinarySearch(Times, time);
            if (index >= 0) return Values[index];
            index = ~index;
            int left = index - 1;
            double amount = (double)(time - Times[left]) / (Times[index] - Times[left]);
            return Values[left] + (Values[index] - Values[left]) * amount;
        }
    }

    private static double Evaluate(Dictionary<char, CurveData> curves, char axis, long time, double fallback) =>
        curves.TryGetValue(axis, out CurveData? curve) ? curve.Evaluate(time, fallback) : fallback;

    private static long[] ToInt64Array(Array? values)
    {
        if (values is null) return [];
        if (values is long[] longs) return longs;
        var result = new long[values.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = Convert.ToInt64(values.GetValue(i), CultureInfo.InvariantCulture);
        return result;
    }

    private static Matrix4x4 BuildLocalTransform(FBXNode model, CoordinateSystem coordinates)
    {
        FBXNode? properties = model.GetChild("Properties70") ?? model.GetChild("Properties60");
        Vector3D translation = ReadVectorProperty(properties, "Lcl Translation", Vector3D.Zero);
        Vector3D rotation = ReadVectorProperty(properties, "Lcl Rotation", Vector3D.Zero);
        Vector3D scale = ReadVectorProperty(properties, "Lcl Scaling", Vector3D.One);
        Vector3D preRotation = ReadVectorProperty(properties, "PreRotation", Vector3D.Zero);
        Vector3D postRotation = ReadVectorProperty(properties, "PostRotation", Vector3D.Zero);
        Vector3D rotationPivot = ReadVectorProperty(properties, "RotationPivot", Vector3D.Zero);
        Vector3D scalingPivot = ReadVectorProperty(properties, "ScalingPivot", Vector3D.Zero);
        Vector3D rotationOffset = ReadVectorProperty(properties, "RotationOffset", Vector3D.Zero);
        Vector3D scalingOffset = ReadVectorProperty(properties, "ScalingOffset", Vector3D.Zero);
        int rotationOrder = ReadIntProperty(properties, "RotationOrder", 0);

        Matrix4x4 scaleMatrix = Matrix4x4.CreateScale(scale);
        Matrix4x4 pre = CreateEulerRotation(preRotation, rotationOrder);
        Matrix4x4 main = CreateEulerRotation(rotation, rotationOrder);
        Matrix4x4 post = CreateEulerRotation(postRotation, rotationOrder);
        if (!Matrix4x4.Invert(post, out Matrix4x4 inversePost))
            inversePost = Matrix4x4.Identity;

        // System.Numerics uses row-vector transforms, so this is the reverse of the
        // FBX SDK's documented column-vector order:
        // T * Roff * Rp * Rpre * R * Rpost^-1 * Rp^-1 * Soff * Sp * S * Sp^-1.
        Matrix4x4 source =
            Matrix4x4.CreateTranslation(-scalingPivot) *
            scaleMatrix *
            Matrix4x4.CreateTranslation(scalingPivot) *
            Matrix4x4.CreateTranslation(scalingOffset) *
            Matrix4x4.CreateTranslation(-rotationPivot) *
            inversePost * main * pre *
            Matrix4x4.CreateTranslation(rotationPivot) *
            Matrix4x4.CreateTranslation(rotationOffset) *
            Matrix4x4.CreateTranslation(translation);

        return coordinates.Matrix(source);
    }

    private static Matrix4x4 BuildGeometryTransform(FBXNode model, CoordinateSystem coordinates)
    {
        FBXNode? properties = model.GetChild("Properties70") ?? model.GetChild("Properties60");
        Vector3D translation = ReadVectorProperty(properties, "GeometricTranslation", Vector3D.Zero);
        Vector3D rotation = ReadVectorProperty(properties, "GeometricRotation", Vector3D.Zero);
        Vector3D scale = ReadVectorProperty(properties, "GeometricScaling", Vector3D.One);
        int rotationOrder = ReadIntProperty(properties, "RotationOrder", 0);

        // FBX geometric transforms affect only the node attribute, not child nodes.
        Matrix4x4 source = Matrix4x4.CreateScale(scale) *
                           CreateEulerRotation(rotation, rotationOrder) *
                           Matrix4x4.CreateTranslation(translation);
        return coordinates.Matrix(source);
    }

    private static Matrix4x4 CreateEulerRotation(Vector3D degrees, int order)
    {
        float x = degrees.X * (MathF.PI / 180f);
        float y = degrees.Y * (MathF.PI / 180f);
        float z = degrees.Z * (MathF.PI / 180f);
        Matrix4x4 rx = Matrix4x4.CreateRotationX(x);
        Matrix4x4 ry = Matrix4x4.CreateRotationY(y);
        Matrix4x4 rz = Matrix4x4.CreateRotationZ(z);
        return order switch
        {
            1 => rx * rz * ry,
            2 => ry * rz * rx,
            3 => ry * rx * rz,
            4 => rz * rx * ry,
            5 => rz * ry * rx,
            _ => rx * ry * rz
        };
    }

    private sealed class CoordinateSystem
    {
        private readonly int _coordAxis;
        private readonly int _coordSign;
        private readonly int _upAxis;
        private readonly int _upSign;
        private readonly int _frontAxis;
        private readonly int _frontSign;
        private readonly Matrix4x4 _basis;
        private readonly Matrix4x4 _inverseBasis;

        public float UnitScale { get; }
        public bool FlipsWinding { get; }

        private CoordinateSystem(int coordAxis, int coordSign, int upAxis, int upSign,
            int frontAxis, int frontSign, float unitScale)
        {
            _coordAxis = coordAxis; _coordSign = coordSign;
            _upAxis = upAxis; _upSign = upSign;
            _frontAxis = frontAxis; _frontSign = frontSign;
            UnitScale = unitScale;

            Vector3D x = Direction(Vector3D.UnitX);
            Vector3D y = Direction(Vector3D.UnitY);
            Vector3D z = Direction(Vector3D.UnitZ);
            _basis = new Matrix4x4(
                x.X, x.Y, x.Z, 0,
                y.X, y.Y, y.Z, 0,
                z.X, z.Y, z.Z, 0,
                0, 0, 0, 1);
            if (!Matrix4x4.Invert(_basis, out Matrix4x4 inverseBasis))
                throw new InvalidOperationException("The FBX axis conversion basis is not invertible.");
            _inverseBasis = inverseBasis;
            float determinant = _basis.GetDeterminant();
            FlipsWinding = determinant < 0;
        }

        public static CoordinateSystem Read(FBXNode? globalSettings, List<FBXImportDiagnostic> diagnostics)
        {
            FBXNode? properties = globalSettings?.GetChild("Properties70");
            int coord = ReadIntProperty(properties, "CoordAxis", 0);
            int coordSign = ReadIntProperty(properties, "CoordAxisSign", 1);
            int up = ReadIntProperty(properties, "UpAxis", 1);
            int upSign = ReadIntProperty(properties, "UpAxisSign", 1);
            int front = ReadIntProperty(properties, "FrontAxis", 2);
            int frontSign = ReadIntProperty(properties, "FrontAxisSign", -1);
            float unit = ReadFloatProperty(properties, "UnitScaleFactor", 1f);

            bool valid = coord is >= 0 and <= 2 && up is >= 0 and <= 2 && front is >= 0 and <= 2 &&
                         coord != up && coord != front && up != front &&
                         Math.Abs(coordSign) == 1 && Math.Abs(upSign) == 1 && Math.Abs(frontSign) == 1;
            if (!valid)
            {
                diagnostics.Add(new FBXImportDiagnostic(FBXDiagnosticSeverity.Warning,
                    "Invalid FBX axis metadata was found; the FBX default axis convention was used."));
                coord = 0; coordSign = 1; up = 1; upSign = 1; front = 2; frontSign = -1;
            }
            if (!float.IsFinite(unit) || unit <= 0f || unit > 1_000_000f)
            {
                diagnostics.Add(new FBXImportDiagnostic(FBXDiagnosticSeverity.Warning,
                    "Invalid FBX UnitScaleFactor was found; centimeters were assumed."));
                unit = 1f;
            }
            return new CoordinateSystem(coord, coordSign, up, upSign, front, frontSign, unit);
        }

        private static float Component(Vector3D value, int axis) => axis switch
        {
            0 => value.X, 1 => value.Y, 2 => value.Z,
            _ => throw new ArgumentOutOfRangeException(nameof(axis))
        };

        public Vector3D Direction(Vector3D source) => new(
            Component(source, _frontAxis) * _frontSign,
            Component(source, _coordAxis) * _coordSign,
            Component(source, _upAxis) * _upSign);

        public Vector3D Position(Vector3D source) => Direction(source) * UnitScale;

        public Vector3D Scale(Vector3D source) => new(
            Component(source, _frontAxis),
            Component(source, _coordAxis),
            Component(source, _upAxis));

        public Quaternion Rotation(Vector3D eulerDegrees, int order)
        {
            Matrix4x4 source = CreateEulerRotation(eulerDegrees, order);
            Matrix4x4 converted = _inverseBasis * source * _basis;
            return Quaternion.Normalize(Quaternion.RotationMatrix(converted));
        }

        public Matrix4x4 Matrix(Matrix4x4 source)
        {
            Matrix4x4 converted = _inverseBasis * source * _basis;
            converted.M41 *= UnitScale;
            converted.M42 *= UnitScale;
            converted.M43 *= UnitScale;
            return converted;
        }
    }

    private static Matrix4x4 ReadMatrix(double[]? values, CoordinateSystem coordinates)
    {
        if (values is null || values.Length < 16)
            return Matrix4x4.Identity;
        var matrix = new Matrix4x4(
            (float)values[0], (float)values[1], (float)values[2], (float)values[3],
            (float)values[4], (float)values[5], (float)values[6], (float)values[7],
            (float)values[8], (float)values[9], (float)values[10], (float)values[11],
            (float)values[12], (float)values[13], (float)values[14], (float)values[15]);
        return coordinates.Matrix(matrix);
    }

    private static void ComputeWorldTransforms(FBXImportedNode[] nodes, List<FBXImportDiagnostic> diagnostics)
    {
        // Use an iterative parent walk: malformed deep hierarchies must not exhaust the call stack.
        var state = new byte[nodes.Length];
        var path = new List<int>();

        for (int startIndex = 0; startIndex < nodes.Length; startIndex++)
        {
            if (state[startIndex] == 2)
                continue;

            path.Clear();
            int current = startIndex;
            while ((uint)current < (uint)nodes.Length && state[current] == 0)
            {
                state[current] = 1;
                path.Add(current);
                current = nodes[current].ParentIndex;
            }

            // Break the edge that closes a detected cycle. This leaves the hierarchy usable
            // and lets the reverse pass compute transforms without recursion.
            if ((uint)current < (uint)nodes.Length && state[current] == 1 && path.Count > 0)
            {
                int cycleEdgeChild = path[^1];
                nodes[cycleEdgeChild].ParentIndex = -1;
                diagnostics.Add(new FBXImportDiagnostic(
                    FBXDiagnosticSeverity.Warning,
                    "A cyclic model hierarchy was detected; one parent link was removed.",
                    nodes[cycleEdgeChild].SourceId));
            }

            for (int i = path.Count - 1; i >= 0; i--)
            {
                int index = path[i];
                int parent = nodes[index].ParentIndex;
                if ((uint)parent < (uint)nodes.Length && state[parent] == 2)
                {
                    // Array element expressions cannot be passed by readonly reference;
                    // local copies also keep the matrix operands explicit for the API.
                    Matrix localTransform = nodes[index].LocalTransform;
                    Matrix parentWorldTransform = nodes[parent].WorldTransform;
                    nodes[index].WorldTransform = Matrix.Multiply(
                        in localTransform, in parentWorldTransform);
                }
                else
                {
                    nodes[index].WorldTransform = nodes[index].LocalTransform;
                }

                state[index] = 2;
            }
        }

        // Rebuild child lists after invalid cyclic links may have been removed.
        var children = new List<int>[nodes.Length];
        for (int i = 0; i < children.Length; i++)
            children[i] = [];
        for (int i = 0; i < nodes.Length; i++)
        {
            int parent = nodes[i].ParentIndex;
            if ((uint)parent < (uint)nodes.Length)
                children[parent].Add(i);
        }
        for (int i = 0; i < nodes.Length; i++)
            nodes[i].Children = children[i].ToArray();
    }

    private static bool IsFinite(Vector3D value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static Vector3D NormalizeOrZero(Vector3D value)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > MathUtilities.ZeroTolerance * MathUtilities.ZeroTolerance &&
               float.IsFinite(lengthSquared)
            ? Vector3D.Normalize(value)
            : Vector3D.Zero;
    }

    private static string GetObjectName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        int separator = name.LastIndexOf("::", StringComparison.Ordinal);
        return separator >= 0 && separator + 2 < name.Length ? name[(separator + 2)..] : name;
    }

    private static string? ReadNodeString(FBXNode? node) => node?.GetString(0);

    private static bool TryReadProperty(FBXNode? properties, string name, out double value)
    {
        if (properties is not null)
        {
            foreach (FBXNode property in properties.GetChildren("P"))
            {
                if (property.GetString(0) != name) continue;
                for (int i = 4; i < property.Properties.Count; i++)
                {
                    object? raw = property.Properties[i];
                    if (raw is null) continue;
                    try
                    {
                        value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                        return true;
                    }
                    catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { }
                }
            }
        }
        value = 0;
        return false;
    }

    private static int ReadIntProperty(FBXNode? properties, string name, int fallback) =>
        TryReadProperty(properties, name, out double value) && double.IsFinite(value) ? (int)value : fallback;

    private static float ReadFloatProperty(FBXNode? properties, string name, float fallback) =>
        TryReadProperty(properties, name, out double value) && double.IsFinite(value) ? (float)value : fallback;

    private static Vector3D ReadVectorProperty(FBXNode? properties, string name, Vector3D fallback)
    {
        if (properties is null)
            return fallback;

        foreach (FBXNode property in properties.GetChildren("P"))
        {
            if (property.GetString(0) != name || property.Properties.Count < 7)
                continue;

            try
            {
                object? x = property.Properties[4];
                object? y = property.Properties[5];
                object? z = property.Properties[6];
                if (x is null || y is null || z is null)
                    continue;

                return new Vector3D(
                    Convert.ToSingle(x, CultureInfo.InvariantCulture),
                    Convert.ToSingle(y, CultureInfo.InvariantCulture),
                    Convert.ToSingle(z, CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                // Ignore a malformed transform property and allow a later property/fallback.
            }
        }

        return fallback;
    }

}
