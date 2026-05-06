using System.Xml.Linq;

namespace AgentWorkspace.Tests.Build;

public sealed class SolutionInventoryTests
{
    [Fact]
    public void Solution_IncludesAllSourceProjects()
    {
        var root = FindRepoRoot();
        var slnx = Path.Combine(root, "AgentWorkspaceTerminal.slnx");

        var actual = XDocument.Load(slnx)
            .Descendants("Project")
            .Select(p => p.Attribute("Path")?.Value)
            .OfType<string>()
            .Where(p => p.Length > 0)
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var expected = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(path => Normalize(Path.GetRelativePath(root, path)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var missing = expected.Where(path => !actual.Contains(path)).ToArray();

        Assert.Empty(missing);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentWorkspaceTerminal.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/');
}
