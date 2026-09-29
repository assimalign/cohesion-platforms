using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

/// <summary>Invokes the application's SDK image target; freshness belongs to the SDK.</summary>
public interface IContainerImagePublisher
{
    /// <summary>Publishes Debug images for a Linux target into the supplied staging directory.</summary>
    /// <param name="projectPath">The apphost project from its own manifest.</param>
    /// <param name="runtimeIdentifier">The target Linux runtime identifier.</param>
    /// <param name="stagingDirectory">The directory receiving the application index and archives.</param>
    /// <param name="cancellationToken">Cancels publication and its child processes.</param>
    /// <returns>A task completing after the SDK target succeeds.</returns>
    /// <exception cref="System.ArgumentException">An invocation argument is invalid.</exception>
    /// <exception cref="System.InvalidOperationException">The SDK target could not start or failed.</exception>
    /// <exception cref="System.OperationCanceledException">Publication was canceled.</exception>
    Task PublishAsync(string projectPath, string runtimeIdentifier, string stagingDirectory,
        CancellationToken cancellationToken = default);
}
