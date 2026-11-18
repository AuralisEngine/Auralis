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
        Objects: {
            Geometry: 1001, "Geometry::Triangle", "Mesh" {
                GeometryVersion: 124
                Vertices: *9 {
                    a: 0,0,0, 100,0,0, 0,100,0
                }
                PolygonVertexIndex: *3 {
                    a: 0,1,-3
                }
            }
            Model: 2001, "Model::Triangle", "Mesh" {
                Version: 232
            }
        }
        Connections: {
            C: "OO",1001,2001
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
    public void RejectsUnterminatedAsciiBlocks()
    {
        byte[] malformed = Encoding.UTF8.GetBytes("Objects: { Geometry: 1, \"Geometry::Bad\", \"Mesh\" {");
        Assert.Throws<FBXFormatException>(() => FBXDocument.Parse(malformed));
    }
}
