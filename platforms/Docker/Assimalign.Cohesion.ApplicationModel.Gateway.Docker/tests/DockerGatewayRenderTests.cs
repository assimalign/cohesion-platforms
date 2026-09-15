using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public partial class DockerGatewayTests
{
    [Fact(DisplayName = "Cohesion Test [Docker] - Render: Should preserve plan order and mount paths without engine or resolver access")]
    public async Task RenderAsync_OnApplicationPlans_ShouldRenderOfflineInPlanOrder()
    {
        // Arrange
        string image = $"team/worker@sha256:{new string('a', 64)}";
        var gateway = new DockerGateway(
            new DockerGatewayOptions { ImageRealizer = new UnusedImageRealizer() },
            () => throw new InvalidOperationException("Render must not contact Docker."));
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), []);
        ResourceManifest first = CreateManifest(image) with
        {
            Name = "z-last-alphabetically",
            Mounts = [new ResourceManifestMount
            {
                Name = "tls", Kind = ResourceMountKind.Secret, ContainerPath = "/run/secrets/tls", Source = "secret:unresolved",
            }],
        };
        builder.AddResource(first);
        ResourceManifest second = CreateManifest(image) with { Name = "a-first-alphabetically" };
        builder.AddResource(second);
        builder.RemoteReference("remote-cache", remote => remote.Endpoint("grpc", "https://cache.example.test:7443"));
        builder.UseGateway(gateway);
        IApplicationModel model = builder.Build().Model;
        using var output = new StringWriter();
        using var repeat = new StringWriter();

        // Act
        await ((IApplicationGatewayRenderer)gateway).RenderAsync([model], output, CancellationToken.None);
        await ((IApplicationGatewayRenderer)gateway).RenderAsync([model], repeat, CancellationToken.None);

        // Assert
        string rendered = output.ToString();
        rendered.ShouldBe(repeat.ToString());
        rendered.IndexOf("appa-z-last-alphabetically", StringComparison.Ordinal)
            .ShouldBeLessThan(rendered.IndexOf("appa-a-first-alphabetically", StringComparison.Ordinal));
        rendered.ShouldContain("/run/secrets/tls", Case.Sensitive);
        rendered.ShouldContain("sensitive: true", Case.Sensitive);
        rendered.ShouldNotContain("bootstrap.token", Case.Sensitive);
        rendered.ShouldNotContain("trust.pem", Case.Sensitive);
        rendered.ShouldNotContain("telemetry.headers", Case.Sensitive);
        rendered.ShouldNotContain("remote-cache", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Render: Should resolve an indexed digest without gathering or reading archives")]
    public async Task RenderAsync_OnImageIndex_ShouldBindRegistryOffline()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-docker-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string digest = $"sha256:{new string('a', 64)}";
            string indexPath = WriteImageIndex(root, "team/worker", digest, "pinned.example:5000");
            var gateway = new DockerGateway(new DockerGatewayOptions
            {
                ImageIndexPath = indexPath,
                ContainerRegistry = "ignored.example:5000",
                ImageRealizer = new UnusedImageRealizer(),
            }, () => throw new InvalidOperationException("Render must not contact Docker."));
            IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), []);
            builder.AddResource(CreateManifest("team/worker@" + digest));
            builder.UseGateway(gateway);
            IApplicationModel model = builder.Build().Model;
            using var output = new StringWriter();

            // Act
            await ((IApplicationGatewayRenderer)gateway).RenderAsync([model], output, CancellationToken.None);

            // Assert
            output.ToString().ShouldContain("pinned.example:5000/team/worker@" + digest, Case.Sensitive);
            output.ToString().ShouldNotContain("ignored.example", Case.Sensitive);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Render: Should honor cancellation before producing output")]
    public async Task RenderAsync_OnCancellation_ShouldLeaveWriterEmpty()
    {
        // Arrange
        var gateway = new DockerGateway();
        using var output = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(() => gateway.RenderAsync([], output, cancellation.Token));
        output.ToString().ShouldBeEmpty();
    }
}
