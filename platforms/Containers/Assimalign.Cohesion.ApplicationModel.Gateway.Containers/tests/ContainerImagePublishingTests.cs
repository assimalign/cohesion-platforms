using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers.Tests;

public class ContainerImagePublishingTests
{
    [Theory(DisplayName = "Cohesion Test [Containers] - Publish: Map target architecture to Linux RID")]
    [InlineData("arm64", "linux-arm64")]
    [InlineData("aarch64", "linux-arm64")]
    [InlineData("amd64", "linux-x64")]
    [InlineData("x86_64", "linux-x64")]
    public void RuntimeIdentifier_OnTargetArchitecture_ShouldMap(string architecture, string expected) =>
        ContainerImagePublishing.RuntimeIdentifier(architecture).ShouldBe(expected);

    [Fact(DisplayName = "Cohesion Test [Containers] - Publish: Invoke SDK target without publishing the running apphost")]
    public void CreateStartInfo_OnImageTarget_ShouldPassSeparateArguments()
    {
        ProcessStartInfo start = MsBuildContainerImagePublisher.CreateStartInfo("app host.csproj", "linux-arm64", "staging dir");
        start.FileName.ShouldBe("dotnet");
        start.ArgumentList.ShouldBe(new[]
        {
            "msbuild", Path.GetFullPath("app host.csproj"), "-restore", "-t:CohesionPublishImages", "-p:Configuration=Debug",
            "-p:CohesionImageRuntimeIdentifier=linux-arm64", "-p:RuntimeIdentifier=linux-arm64",
            $"-p:PublishDir={Path.GetFullPath("staging dir")}{Path.DirectorySeparatorChar}",
        });
        start.UseShellExecute.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Publish: Invoke every run and reload the resulting index")]
    public async Task PrepareAsync_OnLocalSource_ShouldLetSdkDecideFreshness()
    {
        using var directory = new TestDirectory();
        var model = new Model(directory.GetPath("apphost.csproj"));
        var publisher = new Publisher();
        string? first = await ContainerImagePublishing.PrepareAsync(model, null,
            _ => Task.FromResult("arm64"), publisher, CancellationToken.None);
        publisher.Rid.ShouldBe("linux-arm64");
        first.ShouldEndWith(Path.Combine("Debug", "linux-arm64", "application.images.json"));
        await ContainerImagePublishing.PrepareAsync(model, null,
            _ => Task.FromResult("arm64"), publisher, CancellationToken.None);
        publisher.Calls.ShouldBe(2);
        var index = await ContainerImagePublishing.ReloadAsync(first!, model.Name, CancellationToken.None);
        index.Application.ShouldBe(model.Name);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Publish: Explicit index skips publisher and target inspection")]
    public async Task PrepareAsync_OnExplicitIndex_ShouldSkipPublication()
    {
        using var directory = new TestDirectory();
        string path = directory.GetPath("application.images.json");
        await File.WriteAllTextAsync(path, "{\"schema\":\"cohesion/images/v1\",\"application\":\"appa\",\"images\":[]}");
        var publisher = new Publisher();
        await ContainerImagePublishing.PrepareAsync(new Model("unused.csproj"), path,
            _ => throw new InvalidOperationException("Must not inspect target"), publisher, CancellationToken.None);
        publisher.Calls.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Containers] - Publish: Deployable environments never publish source images")]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task PrepareAsync_OnNonLocalModel_ShouldSkipPublication(string environment)
    {
        var publisher = new Publisher();
        string? path = await ContainerImagePublishing.PrepareAsync(new Model("unused.csproj", environment), null,
            _ => throw new InvalidOperationException("Must not inspect target"), publisher, CancellationToken.None);
        path.ShouldBeNull();
        publisher.Calls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Containers] - Publish: Failure prevents stale index reuse")]
    public async Task PrepareAsync_OnPublisherFailure_ShouldPropagateFailure()
    {
        using var directory = new TestDirectory();
        await Should.ThrowAsync<IOException>(() => ContainerImagePublishing.PrepareAsync(new Model(directory.GetPath("apphost.csproj")), null,
            _ => Task.FromResult("amd64"), new Publisher { Fail = true }, CancellationToken.None));
    }

    private sealed class Publisher : IContainerImagePublisher
    {
        public int Calls { get; private set; }
        public string? Rid { get; private set; }
        public bool Fail { get; init; }
        public async Task PublishAsync(string projectPath, string runtimeIdentifier, string stagingDirectory, CancellationToken cancellationToken = default)
        {
            if (Fail) { throw new IOException("Publisher failed"); }
            Calls++;
            Rid = runtimeIdentifier;
            await File.WriteAllTextAsync(Path.Combine(stagingDirectory, "application.images.json"),
                "{\"schema\":\"cohesion/images/v1\",\"application\":\"appa\",\"images\":[]}", cancellationToken);
        }
    }

    private sealed class Model(string project, string environment = "Local") : IApplicationModel
    {
        public ApplicationName Name => "appa";
        public IApplicationEnvironment Environment => Application.CreateBuilder(Name, ["--environment", environment]).Environment;
        public GatewayRunMode RunMode => GatewayRunMode.Apply;
        public ResourceName GatewayIdentity => "test";
        public string Owner => "appa@test";
        public bool Adopt => false;
        public bool RestartOrphans => false;
        public IReadOnlyList<IApplicationResourceDescriptor> Descriptors => [];
        public IReadOnlyList<IApplicationResource> Resources => [];
        public IReadOnlyList<ResourcePlan> Plans => [];
        public IReadOnlyList<ResourceManifest> Manifests => [new ResourceManifest
        {
            Application = "appa", ApplicationModel = "Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane",
            Artifact = new ResourceManifestArtifact { Project = project },
        }];
    }
}
