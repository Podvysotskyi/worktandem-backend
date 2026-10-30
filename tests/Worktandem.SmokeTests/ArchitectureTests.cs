using System.Xml.Linq;
using Xunit;

namespace Worktandem.SmokeTests;

public class ArchitectureTests
{
    // These executable dependency rules describe the intentionally small initial graph.
    // Update deliberately when introducing a real dependency, not to silence a failure.
    [Theory]
    [InlineData("Domain", "")]
    [InlineData("Application", "Domain")]
    [InlineData("Infrastructure", "Application")]
    [InlineData("Api", "Application,Infrastructure")]
    public void Organization_projects_reference_only_their_allowed_inner_layers(string layer, string allowed)
    {
        AssertLayers("Organization", layer, allowed);
        AssertLayers("Identity", layer, allowed);
    }

    private static void AssertLayers(string service, string layer, string allowed)
    {
        var project = ReadProject($"Worktandem.{service}.{layer}");
        var actual = project.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension((string)reference.Attribute("Include")!))
            .OrderBy(name => name).ToArray();
        var expected = allowed.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(name => $"Worktandem.{service}.{name}").OrderBy(name => name).ToArray();
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Domain")]
    [InlineData("Application")]
    public void Inner_layers_do_not_depend_on_web_or_persistence_frameworks(string layer)
    {
        foreach (var service in new[] { "Identity", "Organization" })
        {
            var project = ReadProject($"Worktandem.{service}.{layer}");
            Assert.Equal("Microsoft.NET.Sdk", (string?)project.Root!.Attribute("Sdk"));
            Assert.Empty(project.Descendants("PackageReference"));
            Assert.Empty(project.Descendants("FrameworkReference"));
        }
    }

    [Fact]
    public void Gateway_has_no_service_implementation_references()
    {
        Assert.Empty(ReadProject("Worktandem.Gateway").Descendants("ProjectReference"));
    }

    private static XDocument ReadProject(string name) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Architecture", name, $"{name}.csproj"));
}
