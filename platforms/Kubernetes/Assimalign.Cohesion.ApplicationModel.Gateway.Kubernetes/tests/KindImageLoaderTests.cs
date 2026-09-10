using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KindImageLoaderTests
{
    private static readonly string _digest = $"sha256:{new string('a', 64)}";

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Kind image loader: Should ignore a non-Kind context")]
    public async Task LoadIfKindAsync_OnNonKindContext_ShouldNotRunKind()
    {
        // Arrange
        var commands = new RecordingKindCommandRunner(new KindCommandResult(0, string.Empty, string.Empty));
        var loader = new KindImageLoader(
            new KubernetesGatewayOptions(),
            new FixedContextResolver("production"),
            commands);

        // Act
        KindImageLoadResult result = await loader.LoadIfKindAsync(
            "image.tar",
            _digest,
            CancellationToken.None);

        // Assert
        result.ShouldBe(KindImageLoadResult.NotKind);
        commands.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Kind image loader: Should load one archive per context and digest")]
    public async Task LoadIfKindAsync_OnRepeatedDigest_ShouldRunExactCommandOnce()
    {
        // Arrange
        var commands = new RecordingKindCommandRunner(new KindCommandResult(0, "loaded", string.Empty));
        var loader = new KindImageLoader(
            new KubernetesGatewayOptions(),
            new FixedContextResolver("kind-development"),
            commands);

        // Act
        KindImageLoadResult first = await loader.LoadIfKindAsync(
            "first image.tar",
            _digest,
            CancellationToken.None);
        KindImageLoadResult second = await loader.LoadIfKindAsync(
            "second.tar",
            _digest,
            CancellationToken.None);

        // Assert
        first.ShouldBe(KindImageLoadResult.Loaded);
        second.ShouldBe(KindImageLoadResult.Loaded);
        IReadOnlyList<string> arguments = commands.Calls.ShouldHaveSingleItem();
        arguments.ShouldBe(
        [
            "load",
            "image-archive",
            "first image.tar",
            "--name",
            "development",
        ]);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Kind image loader: Should warn once when Kind is unavailable")]
    public async Task LoadIfKindAsync_OnMissingKindExecutable_ShouldWarnAndSkip()
    {
        // Arrange
        var warnings = new List<string>();
        var options = new KubernetesGatewayOptions { WarningHandler = warnings.Add };
        var commands = new RecordingKindCommandRunner(result: null);
        var loader = new KindImageLoader(
            options,
            new FixedContextResolver("kind-development"),
            commands);

        // Act
        KindImageLoadResult first = await loader.LoadIfKindAsync(
            "first.tar",
            _digest,
            CancellationToken.None);
        KindImageLoadResult second = await loader.LoadIfKindAsync(
            "second.tar",
            $"sha256:{new string('b', 64)}",
            CancellationToken.None);

        // Assert
        first.ShouldBe(KindImageLoadResult.KindUnavailable);
        second.ShouldBe(KindImageLoadResult.KindUnavailable);
        commands.Calls.Count.ShouldBe(1);
        string warning = warnings.ShouldHaveSingleItem();
        warning.ShouldContain("kind' executable", Case.Sensitive);
        warning.ShouldContain("was not loaded", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Kind image loader: Should surface command failure diagnostics")]
    public async Task LoadIfKindAsync_OnFailedKindCommand_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var commands = new RecordingKindCommandRunner(
            new KindCommandResult(17, string.Empty, "provider failed"));
        var loader = new KindImageLoader(
            new KubernetesGatewayOptions(),
            new FixedContextResolver("kind-development"),
            commands);

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => loader.LoadIfKindAsync("image.tar", _digest, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("cluster 'development'", Case.Sensitive);
        exception.Message.ShouldContain("exit code 17", Case.Sensitive);
        exception.Message.ShouldContain("provider failed", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Kind image loader: Should propagate cancellation")]
    public async Task LoadIfKindAsync_OnCancellation_ShouldPropagateOperationCanceledException()
    {
        // Arrange
        var loader = new KindImageLoader(
            new KubernetesGatewayOptions(),
            new FixedContextResolver("kind-development"),
            new CancelingKindCommandRunner());

        // Act
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task action() => loader.LoadIfKindAsync("image.tar", _digest, cancellation.Token);

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(action);
    }

    private sealed class FixedContextResolver : IKubernetesContextResolver
    {
        private readonly string? _context;

        public FixedContextResolver(string? context) => _context = context;

        public string? ResolveCurrentContext(KubernetesGatewayOptions options) => _context;
    }

    private sealed class RecordingKindCommandRunner : IKindCommandRunner
    {
        private readonly KindCommandResult? _result;

        public RecordingKindCommandRunner(KindCommandResult? result) => _result = result;

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<KindCommandResult?> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(arguments.ToArray());
            return Task.FromResult(_result);
        }
    }

    private sealed class CancelingKindCommandRunner : IKindCommandRunner
    {
        public Task<KindCommandResult?> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<KindCommandResult?>(cancellationToken);
    }
}
