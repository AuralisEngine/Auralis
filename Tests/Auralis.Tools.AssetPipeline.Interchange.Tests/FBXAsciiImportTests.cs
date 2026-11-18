using System.Text;
using Auralis.Tools.AssetPipeline.Interchange.FBX;
using Xunit;

namespace Auralis.Tools.AssetPipeline.Interchange.Tests;

public sealed class FBXAsciiImportTests
{
    private const string TriangleScene = """
        ; FBX 7.4.0 project file
        FBXHeaderExtension: {
            FBXHeaderVersion: 1003
            FBXVersion: 7400
        }
        GlobalSettings: {
            Properties70: {
                P: "UpAxis", "int", "Integer", "",1
                P: "UpAxisSign", "int", "Integer", "",1
                P: "FrontAxis", "int", "Integer", "",2
                P: "FrontAxisSign", "int", "Integer", "",-1
                P: "CoordAxis", "int", "Integer", "",0
                P: "CoordAxisSign", "int", "Integer", "",1
                P: "UnitScaleFactor", "double", "Number", "",1
            }
        }
        Objects:
        {
            Geometry: 1001, "Geometry::Triangle", "Mesh" {
                GeometryVersion: 124
                Vertices: *9 {
                    a: 0,0,0, 100,0,0,\n                    0,100,0
                }
                PolygonVertexIndex: *3 {
                    a: 0,1,-3
                }
                LayerElementMaterial: {
                    Materials: *1 {
                        a: 0
                    }
                    MappingInformationType: "ByPolygon"
                    ReferenceInformationType: "IndexToDirect"
                }
            }
            Model: 2001, "Model::Triangle", "Mesh" {
                Version: 232
            }
            Material: 3001, "Material::TestMaterial", "" {
                Properties70: {
                    P: "DiffuseColor", "Color", "", "A", 0.25, 0.5, 0.75
                }
            }
        }
        Connections: {
            C: "OO",1001,2001
            C: "OO",3001,2001
        }
        """;

    [Fact]
    public void ParsesAsciiDocumentAndPreservesObjectGraph()
    {
        FBXDocument document = FBXDocument.Parse(Encoding.UTF8.GetBytes(TriangleScene));

        Assert.False(document.Header.IsBinary);
        Assert.Equal(7400u, document.Header.Version);
        Assert.NotNull(document.GetRootNode("Objects")?.GetChild("Geometry"));
        Assert.Equal(3, document.GetRootNode("Objects")!.GetChild("Geometry")!
            .GetInt32Array("PolygonVertexIndex")!.Length);
    }

    [Fact]
    public void ImportsMeshIntoUnrealCoordinateSpaceAndTriangulates()
    {
        FBXDocument document = FBXDocument.Parse(Encoding.UTF8.GetBytes(TriangleScene));
        FBXImportedScene scene = FBXImporter.Import(document);

        FBXImportedMesh mesh = Assert.Single(scene.Meshes);
        Assert.Equal(3, mesh.Positions.Length);
        Assert.Equal(3, mesh.Indices.Length);
        Assert.All(mesh.Positions, static position => Assert.True(float.IsFinite(position.X + position.Y + position.Z)));
        Assert.Equal(100f, mesh.Positions[1].Y, precision: 3);
        Assert.Equal(100f, mesh.Positions[2].Z, precision: 3);
        Assert.Single(scene.Nodes);
    }

    [Fact]
    public void ImportsMaterialAssignmentsFromDirectPolygonLayer()
    {
        FBXDocument document = FBXDocument.Parse(Encoding.UTF8.GetBytes(TriangleScene));
        FBXImportedScene scene = FBXImporter.Import(document);

        FBXImportedMaterial material = Assert.Single(scene.Materials);
        Assert.Equal(0.25f, material.BaseColor.X, precision: 3);
        Assert.Equal(0.5f, material.BaseColor.Y, precision: 3);
        FBXImportedMesh mesh = Assert.Single(scene.Meshes);
        FBXImportedSubmesh submesh = Assert.Single(mesh.Submeshes);
        Assert.Equal(0, submesh.MaterialIndex);
        Assert.Equal(new[] { 0 }, scene.Nodes[0].MaterialIndices);
    }

    [Fact]
    public void ParsesBinaryNodeRecords()
    {
        using var stream = new MemoryStream();
        byte[] signature = Encoding.ASCII.GetBytes(FBXHeader.BinarySignature);
        stream.Write(signature);
        WriteUInt32(stream, 7400);

        uint nodeStart = (uint)stream.Position;
        uint nodeEnd = nodeStart + 13 + 4 + 5 + 13;
        WriteUInt32(stream, nodeEnd);
        WriteUInt32(stream, 1);
        WriteUInt32(stream, 5);
        stream.WriteByte(4);
        stream.Write(Encoding.ASCII.GetBytes("Test"));
        stream.WriteByte((byte)'I');
        WriteInt32(stream, 42);
        stream.Write(new byte[13]);
        stream.Write(new byte[13]);

        FBXDocument document = FBXDocument.Parse(stream.ToArray());
        Assert.True(document.Header.IsBinary);
        Assert.Equal(7400u, document.Header.Version);
        Assert.Equal(42, document.GetRootNode("Test")!.GetInt64(0));
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    [Fact]
    public void RejectsUnterminatedAsciiBlocks()
    {
        byte[] malformed = Encoding.UTF8.GetBytes("Objects: { Geometry: 1, \"Geometry::Bad\", \"Mesh\" {");
        Assert.Throws<FBXFormatException>(() => FBXDocument.Parse(malformed));
    }
}
