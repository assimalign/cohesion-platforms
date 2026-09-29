# IContainerImagePublisher

Task PublishAsync(string projectPath, string runtimeIdentifier, string stagingDirectory,
CancellationToken cancellationToken = default).

Runs the application's Debug image target. The SDK owns freshness and produces application.images.json
in stagingDirectory. The default implementation is created by ContainerImagePublishing.CreatePublisher().
It passes each process argument separately, relays output/error, reports a nonzero exit, and stops the
child process tree on cancellation. Consumers may implement this seam for publication testing.
