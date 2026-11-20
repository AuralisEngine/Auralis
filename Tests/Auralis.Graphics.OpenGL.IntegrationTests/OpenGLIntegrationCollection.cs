namespace Auralis.Graphics.OpenGL.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OpenGLIntegrationCollection : ICollectionFixture<OpenGLIntegrationFixture>
{
    public const string Name = "OpenGL hardware integration";
}
