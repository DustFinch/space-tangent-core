using System.Reflection;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class AssemblyBoundaryTests
{
    [Theory]
    [InlineData("SpaceTangent.Core")]
    [InlineData("SpaceTangent.Contracts")]
    public void LoadsWithoutAnEngineAssembly(string name)
    {
        var assembly = Assembly.Load(name);

        Assert.All(assembly.GetReferencedAssemblies(), reference =>
        {
            Assert.DoesNotContain("Godot", reference.Name!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Unity", reference.Name!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Unreal", reference.Name!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SpaceTangent.Application", reference.Name!, StringComparison.Ordinal);
        });
    }
}
