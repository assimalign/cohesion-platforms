using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

/// <summary>
/// Describes all resource images gathered for one Cohesion application.
/// </summary>
public interface IApplicationImageIndex
{
    /// <summary>Gets the application that owns the index.</summary>
    ApplicationName Application { get; }

    /// <summary>Gets the resource image entries in deterministic declaration order.</summary>
    IReadOnlyList<IContainerImageIndexEntry> Images { get; }
}
