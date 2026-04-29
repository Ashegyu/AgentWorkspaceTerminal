using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkspace.Abstractions.Templates;

/// <summary>
/// Loads a <see cref="WorkspaceTemplate"/> from a file on disk. Implementations are
/// responsible for parsing (YAML / JSON), schema validation, and cross-ref validation via
/// <see cref="WorkspaceTemplateValidator"/>.
/// </summary>
public interface IWorkspaceTemplateLoader
{
    /// <summary>Loads and fully validates the template at <paramref name="path"/>.</summary>
    /// <exception cref="WorkspaceTemplateException">
    /// Thrown when the file cannot be parsed or fails validation.
    /// </exception>
    ValueTask<WorkspaceTemplate> LoadAsync(string path, CancellationToken ct = default);
}
