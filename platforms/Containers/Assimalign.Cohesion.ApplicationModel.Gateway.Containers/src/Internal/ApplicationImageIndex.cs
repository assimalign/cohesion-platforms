using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

internal sealed class ApplicationImageIndex : IApplicationImageIndex
{
    public ApplicationImageIndex(
        ApplicationName application,
        IReadOnlyList<IContainerImageIndexEntry> images)
    {
        Application = application;
        Images = images;
    }

    public ApplicationName Application { get; }

    public IReadOnlyList<IContainerImageIndexEntry> Images { get; }
}
