using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KubernetesGatewayTests
{
    private static readonly string Image =
        $"registry.example/test@sha256:{new string('a', 64)}";

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Name: Should be the stable identity 'kubernetes'")]
    public void Name_OnGateway_ShouldBeKubernetes()
    {
        // Arrange
        var gateway = new KubernetesGateway();

        // Act
        string name = gateway.Name.Value;

        // Assert
        name.ShouldBe("kubernetes");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Ctor: Should throw ArgumentNullException for null options")]
    public void Ctor_OnNullOptions_ShouldThrowArgumentNullException()
    {
        // Arrange
        KubernetesGatewayOptions options = null!;

        // Act
        var exception = Should.Throw<ArgumentNullException>(() => new KubernetesGateway(options));

        // Assert
        exception.ParamName.ShouldBe("options");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - UseKubernetesGateway: Should accept a v1 resource plan")]
    public void UseKubernetesGateway_OnBuilderWithPlatformController_ShouldBuild()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(CreateManifest());

        // Act
        IApplication application = builder.UseKubernetesGateway().Build();

        // Assert
        application.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - UseKubernetesGateway: Should invoke the configure callback")]
    public void UseKubernetesGateway_OnConfigureOverload_ShouldInvokeConfigureCallback()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(CreateManifest());
        var invoked = false;

        // Act
        var application = builder
            .UseKubernetesGateway(options =>
            {
                invoked = true;
                options.FieldManager = "cohesion-test";
                options.Controllers.Add(new AcceptingController());
            })
            .Build();

        // Assert
        invoked.ShouldBeTrue();
        application.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - UseKubernetesGateway: Should parse Kubernetes connection arguments")]
    public void UseKubernetesGateway_OnPlatformArguments_ShouldApplyConnectionOptions()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(CreateManifest());
        string? contextName = null;
        string? kubeConfigPath = null;

        // Act
        IApplication application = builder
            .UseKubernetesGateway(
                ["--context", "kind-development", "--kubeconfig=cluster.config"],
                options =>
                {
                    contextName = options.ContextName;
                    kubeConfigPath = options.KubeConfigPath;
                })
            .Build();

        // Assert
        application.ShouldNotBeNull();
        contextName.ShouldBe("kind-development");
        kubeConfigPath.ShouldBe("cluster.config");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Build: Should reject a non-manifest resource")]
    public void Build_OnNonManifestResource_ShouldRejectResource()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(new FakeExecutableResource("web"));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => builder.UseKubernetesGateway().Build());

        // Assert
        exception.Message.ShouldContain(nameof(IManifestResource));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Build: Should reject a manifest without an image")]
    public void Build_OnManifestWithoutImage_ShouldRejectResource()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(CreateManifest(image: null));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => builder.UseKubernetesGateway().Build());

        // Assert
        exception.Message.ShouldContain("artifact.image", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Build: Should reject an unpinned image")]
    public void Build_OnUnpinnedImage_ShouldRejectResource()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(CreateManifest("registry.example/test:latest"));

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => builder.UseKubernetesGateway().Build());

        // Assert
        exception.Message.ShouldContain("@sha256:", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Build: Should reject an unpinned image even with a custom realizer")]
    public void Build_OnUnpinnedImageWithRealizer_ShouldRejectResource()
    {
        // Arrange
        var realizer = new FakeImageRealizer(
            new FakeContainerImageArtifact(
                ResourceIdOf("other"),
                "registry.example/test",
                $"sha256:{new string('b', 64)}",
                "latest"));
        var gateway = new KubernetesGateway(new KubernetesGatewayOptions
        {
            ImageRealizer = realizer,
            TrustKeyRepository = new TestGatewayTrustKeys(),
        });
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(CreateManifest("application.images.json:web"));
        builder.UseGateway(gateway);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("@sha256:", Case.Sensitive);
        realizer.CallCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Build: Should require a digest-pinned manifest when an image index is configured")]
    public void Build_OnTagOnlyImageWithIndex_ShouldRejectResource()
    {
        // Arrange
        var realizer = new FakeImageRealizer(
            new FakeContainerImageArtifact(
                ResourceIdOf("web"),
                "registry.example/test",
                $"sha256:{new string('b', 64)}",
                "latest"));
        var gateway = new KubernetesGateway(new KubernetesGatewayOptions
        {
            ImageIndexPath = "application.images.json",
            ImageRealizer = realizer,
            TrustKeyRepository = new TestGatewayTrustKeys(),
        });
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(CreateManifest("registry.example/test:latest"));
        builder.UseGateway(gateway);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("@sha256:", Case.Sensitive);
        realizer.CallCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather: Should reject an unpinned artifact returned by the image realizer")]
    public async Task GatherAsync_OnUnpinnedRealizedArtifact_ShouldRejectArtifact()
    {
        // Arrange
        var realizer = new FakeImageRealizer(
            new FakeContainerImageArtifact(
                ResourceIdOf("web"),
                "registry.example/test",
                "sha256:not-a-digest",
                "latest"));
        var gateway = new KubernetesGateway(new KubernetesGatewayOptions
        {
            ImageRealizer = realizer,
            TrustKeyRepository = new TestGatewayTrustKeys(),
        });
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(CreateManifest(Image));
        builder.UseGateway(gateway);
        IApplication application = builder.Build();

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            () => ((IApplicationGateway)gateway).StartAsync(application.Model));

        // Assert
        exception.Message.ShouldContain("64 hexadecimal characters");
        realizer.CallCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather: Should reject an artifact for a different resource")]
    public async Task GatherAsync_OnArtifactForDifferentResource_ShouldRejectArtifact()
    {
        // Arrange
        var realizer = new FakeImageRealizer(
            new FakeContainerImageArtifact(
                ResourceIdOf("other"),
                "registry.example/test",
                $"sha256:{new string('b', 64)}",
                "latest"));
        var gateway = new KubernetesGateway(new KubernetesGatewayOptions
        {
            ImageRealizer = realizer,
            TrustKeyRepository = new TestGatewayTrustKeys(),
        });
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(CreateManifest(Image));
        builder.UseGateway(gateway);
        IApplication application = builder.Build();

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => ((IApplicationGateway)gateway).StartAsync(application.Model));

        // Assert
        exception.Message.ShouldContain("artifact for resource");
        exception.Message.ShouldContain(ResourceIdOf("other").ToString());
        realizer.CallCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Gather: Should reject a different image returned by a custom realizer")]
    public async Task GatherAsync_OnDifferentRealizedImage_ShouldRejectArtifact()
    {
        // Arrange
        string otherDigest = $"sha256:{new string('b', 64)}";
        var realizer = new FakeImageRealizer(
            new FakeContainerImageArtifact(
                ResourceIdOf("web"),
                "registry.example/test",
                otherDigest,
                null));
        var gateway = new KubernetesGateway(new KubernetesGatewayOptions
        {
            ImageRealizer = realizer,
            TrustKeyRepository = new TestGatewayTrustKeys(),
        });
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(CreateManifest(Image));
        builder.UseGateway(gateway);
        IApplication application = builder.Build();

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => ((IApplicationGateway)gateway).StartAsync(application.Model));

        // Assert
        exception.Message.ShouldContain(otherDigest, Case.Sensitive);
        exception.Message.ShouldContain(Image, Case.Sensitive);
        exception.Message.ShouldContain("manifest artifact", Case.Sensitive);
        realizer.CallCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Build: Should accept external resources without image manifests")]
    public void Build_OnExternalResource_ShouldAcceptExternalDescriptor()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor local = builder.AddResource(CreateManifest());
        IApplicationResourceDescriptor external = builder.RemoteReference(
            "catalog",
            remote => remote.Endpoint("http", "http://catalog.example.test:8080"));
        local.DependsOn(external);

        // Act
        IApplication application = builder.UseKubernetesGateway().Build();

        // Assert
        application.Model.Resources.ShouldContain(resource => resource is IExternalResource);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - UseKubernetesGateway: Should throw ArgumentNullException for null configure")]
    public void UseKubernetesGateway_OnNullConfigure_ShouldThrowArgumentNullException()
    {
        // Arrange
        var builder = Application.CreateBuilder();

        // Act
        var exception = Should.Throw<ArgumentNullException>(() => builder.UseKubernetesGateway(null!));

        // Assert
        exception.ParamName.ShouldBe("configure");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Options: Should default the server-side-apply field manager to 'cohesion-gateway'")]
    public void Options_OnDefaults_ShouldUseCohesionGatewayFieldManager()
    {
        // Arrange & Act
        var options = new KubernetesGatewayOptions();

        // Assert
        options.FieldManager.ShouldBe("cohesion-gateway");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Options: Should default the readiness and stop budgets")]
    public void Options_OnDefaults_ShouldUseDefaultBudgets()
    {
        // Arrange & Act
        var options = new KubernetesGatewayOptions();

        // Assert
        options.ReadinessBudget.ShouldBe(TimeSpan.FromSeconds(60));
        options.StopGrace.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Options: Should default kubeconfig path and context to null")]
    public void Options_OnDefaults_ShouldLeaveKubeConfigResolutionConventional()
    {
        // Arrange & Act
        var options = new KubernetesGatewayOptions();

        // Assert
        options.KubeConfigPath.ShouldBeNull();
        options.ContextName.ShouldBeNull();
        options.ImageIndexPath.ShouldBeNull();
        options.ContainerRegistry.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Options: Should reject an empty image index path")]
    public void Ctor_OnEmptyImageIndexPath_ShouldThrowArgumentException()
    {
        // Arrange
        var options = new KubernetesGatewayOptions { ImageIndexPath = " " };

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => new KubernetesGateway(options));

        // Assert
        exception.ParamName.ShouldBe(nameof(KubernetesGatewayOptions.ImageIndexPath));
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - Options: Should reject a registry value that is not an authority")]
    [InlineData("https://registry.example.test")]
    [InlineData("registry.example.test/team")]
    [InlineData(" ")]
    public void Ctor_OnInvalidContainerRegistry_ShouldThrowArgumentException(string registry)
    {
        // Arrange
        var options = new KubernetesGatewayOptions { ContainerRegistry = registry };

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => new KubernetesGateway(options));

        // Assert
        exception.ParamName.ShouldBe(nameof(KubernetesGatewayOptions.ContainerRegistry));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Import: Should reject invalid Kubernetes options before resolution")]
    public void ImportFromKubernetes_OnInvalidOptions_ShouldThrowArgumentException()
    {
        // Arrange
        var options = new KubernetesGatewayOptions { ContextName = " " };

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => KubernetesApplicationModelResolvers.ImportFromKubernetes("appa", options));

        // Assert
        exception.ParamName.ShouldBe(nameof(KubernetesGatewayOptions.ContextName));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Export: Should add, change, and withdraw a public Service address")]
    public void CreateExportEndpoints_OnLoadBalancerChanges_ShouldRefreshPublicAddress()
    {
        ResourceManifest manifest = CreateManifest();
        var internalEndpoint = new ResourceEndpoint(
            "http",
            "http",
            8080,
            false,
            "web-http.appa.svc");

        ApplicationExportEndpoint added = KubernetesGateway.CreateExportEndpoints(
            [
                internalEndpoint,
                new ResourceEndpoint("http", "https", 443, true, "first.example.test"),
            ],
            manifest).Single();
        ApplicationExportEndpoint changed = KubernetesGateway.CreateExportEndpoints(
            [
                internalEndpoint,
                new ResourceEndpoint("http", "https", 443, true, "second.example.test"),
            ],
            manifest).Single();
        ApplicationExportEndpoint withdrawn = KubernetesGateway.CreateExportEndpoints(
            [internalEndpoint],
            manifest).Single();

        added.Internal.ShouldBe("web-http.appa.svc:8080");
        added.Public.ShouldBe("https://first.example.test:443");
        changed.Public.ShouldBe("https://second.example.test:443");
        withdrawn.Public.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Session: Should reject resource names that collide after Kubernetes normalization")]
    public void ValidateNormalizedNames_OnCaseDistinctResources_ShouldRejectBeforeMutation()
    {
        IApplicationModel model = CreateModel("appa", "Worker", "worker");

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => KubernetesGateway.ValidateNormalizedNames([model]));

        exception.Message.ShouldContain("both normalize to Kubernetes resource name 'worker'");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Session: Should reject application names that collide after Kubernetes normalization")]
    public void ValidateNormalizedNames_OnCaseDistinctApplications_ShouldRejectBeforeMutation()
    {
        IApplicationModel upper = new RenamedModel(
            CreateModel("appa", "web"),
            ApplicationName.Parse("AppA"));
        IApplicationModel lower = CreateModel("appa", "web");

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => KubernetesGateway.ValidateNormalizedNames([upper, lower]));

        exception.Message.ShouldContain("both normalize to Kubernetes namespace 'appa'");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Session: Should reject Service names shared by two plans")]
    public void ValidateNormalizedNames_OnSharedServiceName_ShouldRejectBeforeMutation()
    {
        IApplicationModel inner = CreateModel("appa", "first", "second");
        ResourcePlan[] plans = inner.Plans
            .Select(plan => CopyPlan(
                plan,
                services: [new ServiceSpec("shared-http", "http", 8080, "tcp", false, false)]))
            .ToArray();
        var model = new PlanOverrideModel(inner, plans);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => KubernetesGateway.ValidateNormalizedNames([model]));

        exception.Message.ShouldContain("both compile Kubernetes Service/shared-http");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Session: Should reject standalone claim names shared by two plans")]
    public void ValidateNormalizedNames_OnSharedStandaloneClaimName_ShouldRejectBeforeMutation()
    {
        IApplicationModel inner = CreateModel("appa", "first", "second");
        ResourcePlan[] plans = inner.Plans
            .Select(plan => CopyPlan(
                plan,
                volumes: [new VolumeSpec("data", ResourceMountKind.Volume, "1Gi", false)]))
            .ToArray();
        var model = new PlanOverrideModel(inner, plans);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => KubernetesGateway.ValidateNormalizedNames([model]));

        exception.Message.ShouldContain("both compile PersistentVolumeClaim/data");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Session: Should reject colliding StatefulSet claim prefixes")]
    public void ValidateNormalizedNames_OnCollidingStatefulClaimPrefixes_ShouldRejectBeforeMutation()
    {
        IApplicationModel inner = CreateModel("appa", "b", "a-b");
        ResourcePlan first = CopyPlan(
            inner.Plans[0],
            workload: inner.Plans[0].Workload with { Kind = WorkloadKind.StatefulSet },
            volumes: [new VolumeSpec("x-a", ResourceMountKind.Volume, "1Gi", true)]);
        ResourcePlan second = CopyPlan(
            inner.Plans[1],
            workload: inner.Plans[1].Workload with { Kind = WorkloadKind.StatefulSet },
            volumes: [new VolumeSpec("x", ResourceMountKind.Volume, "1Gi", true)]);
        var model = new PlanOverrideModel(inner, [first, second]);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => KubernetesGateway.ValidateNormalizedNames([model]));

        exception.Message.ShouldContain("both compile StatefulSet claim prefix 'x-a-b'");
    }

    private static IApplicationBuilder CreateBuilder() =>
        Application.CreateBuilder(ApplicationName.Parse("appa"), []);

    private static ResourcePlan CopyPlan(
        ResourcePlan source,
        WorkloadSpec? workload = null,
        IReadOnlyList<VolumeSpec>? volumes = null,
        IReadOnlyList<ServiceSpec>? services = null) => new(
            source.Schema,
            source.Resource,
            source.Kind,
            workload ?? source.Workload,
            source.Container,
            volumes ?? source.Volumes,
            services ?? source.Services,
            source.Exposures,
            source.Hints,
            source.ControlPlane);

    private static ResourceId ResourceIdOf(string resource) =>
        ((IApplicationResource)new FakeExecutableResource(resource)).Id;

    private static ResourceManifest CreateManifest() => CreateManifest(Image);

    private static ResourceManifest CreateManifest(string? image) =>
        CreateManifest("web", "appa", image);

    private static ResourceManifest CreateManifest(
        string name,
        string application,
        string? image) => new()
        {
            Name = name,
            Kind = "Web",
            Application = application,
            ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Assimalign.Cohesion.Test.Application",
                Image = image,
            },
            Endpoints =
        [
            new ResourceManifestEndpoint
            {
                Name = "http",
                Scheme = "http",
                Protocol = "tcp",
                ContainerPort = 8080,
            },
        ],
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "http",
                Path = "/cohesion/v1",
            },
        };

    private static IApplicationModel CreateModel(
        string application,
        params string[] resources)
    {
        IApplicationBuilder builder = Application.CreateBuilder(
            ApplicationName.Parse(application),
            []);
        for (int index = 0; index < resources.Length; index++)
        {
            builder.AddResource(CreateManifest(resources[index], application, Image));
        }

        return builder
            .UseKubernetesGateway(options => options.Controllers.Add(new AcceptingController()))
            .Build()
            .Model;
    }

    private sealed class RenamedModel : IApplicationModel
    {
        private readonly IApplicationModel _inner;

        public RenamedModel(IApplicationModel inner, ApplicationName name)
        {
            _inner = inner;
            Name = name;
        }

        public ApplicationName Name { get; }
        public IApplicationEnvironment Environment => _inner.Environment;
        public GatewayRunMode RunMode => _inner.RunMode;
        public ResourceName GatewayIdentity => _inner.GatewayIdentity;
        public string Owner => $"{Name}@{GatewayIdentity}";
        public bool Adopt => _inner.Adopt;
        public bool RestartOrphans => _inner.RestartOrphans;
        public IReadOnlyList<IApplicationResourceDescriptor> Descriptors => _inner.Descriptors;
        public IReadOnlyList<IApplicationResource> Resources => _inner.Resources;
        public IReadOnlyList<ResourceManifest> Manifests => _inner.Manifests;
        public IReadOnlyList<ResourcePlan> Plans => _inner.Plans;
    }

    private sealed class PlanOverrideModel : IApplicationModel
    {
        private readonly IApplicationModel _inner;

        public PlanOverrideModel(IApplicationModel inner, IReadOnlyList<ResourcePlan> plans)
        {
            _inner = inner;
            Plans = plans;
            Descriptors = inner.Descriptors
                .Select((descriptor, index) =>
                    (IApplicationResourceDescriptor)new PlanOverrideDescriptor(
                        descriptor.Resource,
                        plans[index]))
                .ToArray();
        }

        public ApplicationName Name => _inner.Name;
        public IApplicationEnvironment Environment => _inner.Environment;
        public GatewayRunMode RunMode => _inner.RunMode;
        public ResourceName GatewayIdentity => _inner.GatewayIdentity;
        public string Owner => _inner.Owner;
        public bool Adopt => _inner.Adopt;
        public bool RestartOrphans => _inner.RestartOrphans;
        public IReadOnlyList<IApplicationResourceDescriptor> Descriptors { get; }
        public IReadOnlyList<IApplicationResource> Resources => _inner.Resources;
        public IReadOnlyList<ResourceManifest> Manifests => _inner.Manifests;
        public IReadOnlyList<ResourcePlan> Plans { get; }
    }

    private sealed class PlanOverrideDescriptor : IApplicationResourceDescriptor
    {
        public PlanOverrideDescriptor(IApplicationResource resource, ResourcePlan plan)
        {
            Resource = resource;
            Plan = plan;
        }

        public IApplicationResource Resource { get; }
        public ResourcePlan? Plan { get; }
        public IReadOnlyList<IApplicationResourceDescriptor> Dependencies => [];
        public IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource) =>
            throw new NotSupportedException();
        public IApplicationResourceDescriptor DependsOn(params IApplicationResourceDescriptor[] resources) =>
            throw new NotSupportedException();
    }

    private sealed class FakeImageRealizer : IImageRealizer
    {
        private readonly IContainerImageArtifact _artifact;

        public FakeImageRealizer(IContainerImageArtifact artifact)
        {
            _artifact = artifact;
        }

        public int CallCount { get; private set; }

        public Task<IContainerImageArtifact> RealizeAsync(
            ResourceId resource,
            string imageReference,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_artifact);
        }
    }

    private sealed record FakeContainerImageArtifact(
        ResourceId Resource,
        string Repository,
        string Digest,
        string? Tag) : IContainerImageArtifact;

    private sealed class AcceptingController : IApplicationResourceController
    {
        public bool CanRealize(ResourcePlan plan, out string? reason)
        {
            reason = null;
            return true;
        }

        public Task ReconcileAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
