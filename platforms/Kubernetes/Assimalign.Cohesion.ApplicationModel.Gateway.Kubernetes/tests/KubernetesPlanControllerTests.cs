using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;
using Assimalign.Cohesion.Core;

using k8s;
using k8s.Models;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KubernetesPlanControllerTests
{
    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should apply the compiled set in dependency order")]
    public async Task ReconcileAsync_OnV1Plan_ShouldApplyInDependencyOrder()
    {
        var api = new FakeApi();
        var observations = new FakeObservations();
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            observations);
        FakeContext context = CreateContext();

        await controller.ReconcileAsync(context);

        api.Applied.ShouldBe(["ConfigMap", "Secret", "Deployment"]);
        observations.Registered.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Contract: Should roll the workload when an observed public URL changes")]
    public async Task RefreshRuntimeEnvironmentAsync_OnPublicExposure_ShouldInjectUrlAndRollPods()
    {
        var api = new FakeApi { PersistAppliedObjects = true };
        var observations = new FakeObservations();
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            observations);
        FakeContext context = CreateContext(expose: true);
        await controller.ReconcileAsync(context);
        string initialRevision = api.AppliedObjects
            .OfType<V1Deployment>()
            .Single()
            .Spec.Template.Metadata.Annotations[
                KubernetesMetadata.RuntimeInputRevisionAnnotation];
        context.State.SetState(
            context.Resource.Id,
            ResourceLifecycle.Running,
            observedEndpoints:
            [
                new ResourceEndpoint("http", "http", 8080, Host: "worker-http.appa.svc"),
                new ResourceEndpoint(
                    "http",
                    "http",
                    8080,
                    IsPublic: true,
                    Host: "worker.example.test"),
            ]);
        api.ClearRecordings();

        KubernetesPlanCompilation refreshed = await controller
            .RefreshRuntimeEnvironmentAsync(
                context,
                observations.LastCompilation!,
                context.State.GetObservedEndpoints(context.Resource.Id),
                CancellationToken.None);

        refreshed.Objects.OfType<V1ConfigMap>().Single().Data[
            ResourceEnvironment.Endpoint("http", "PUBLIC_URL")]
            .ShouldBe("http://worker.example.test:8080");
        string refreshedRevision = refreshed.Objects
            .OfType<V1Deployment>()
            .Single()
            .Spec.Template.Metadata.Annotations[
                KubernetesMetadata.RuntimeInputRevisionAnnotation];
        refreshedRevision.ShouldNotBe(initialRevision);
        api.JsonPatches.Select(static item => item.Kind)
            .ShouldBe(["ConfigMap", "Deployment"]);
        api.JsonPatches[0].Patch.ShouldContain(
            "\"op\":\"test\",\"path\":\"/metadata/resourceVersion\"");
        api.JsonPatches[0].Patch.ShouldNotContain("bootstrap");
        observations.RegisterCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Contract: Should replace an immutable Job when its public URL changes")]
    public async Task RefreshRuntimeEnvironmentAsync_OnJobPublicExposure_ShouldRecreateJob()
    {
        var api = new FakeApi { PersistAppliedObjects = true };
        var observations = new FakeObservations();
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            observations);
        FakeContext context = CreateContext(WorkloadKind.Job, expose: true);
        await controller.ReconcileAsync(context);
        context.State.SetState(
            context.Resource.Id,
            ResourceLifecycle.Running,
            observedEndpoints:
            [
                new ResourceEndpoint("http", "http", 8080, Host: "worker-http.appa.svc"),
                new ResourceEndpoint(
                    "http",
                    "http",
                    8080,
                    IsPublic: true,
                    Host: "worker.example.test"),
            ]);
        api.ClearRecordings();

        KubernetesPlanCompilation refreshed = await controller.RefreshRuntimeEnvironmentAsync(
            context,
            observations.LastCompilation!,
            context.State.GetObservedEndpoints(context.Resource.Id),
            CancellationToken.None);

        api.Deleted.ShouldBe(["Job"]);
        api.Applied.ShouldBe(["Job"]);
        refreshed.Objects.OfType<V1ConfigMap>().Single().Data[
            ResourceEnvironment.Endpoint("http", "PUBLIC_URL")]
            .ShouldBe("http://worker.example.test:8080");
        api.GetPersisted<V1Job>().Spec.Template.Metadata.Annotations[
            KubernetesMetadata.RuntimeInputRevisionAnnotation]
            .ShouldBe(refreshed.Objects.OfType<V1Job>().Single().Spec.Template.Metadata.Annotations[
                KubernetesMetadata.RuntimeInputRevisionAnnotation]);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Contract: Should serialize public endpoint refresh with a newer reconcile")]
    public async Task ReconcileAsync_DuringPublicEndpointRefresh_ShouldLeaveNewestInputsAndAddress()
    {
        var api = new FakeApi { PersistAppliedObjects = true };
        var observations = new FakeObservations();
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            observations);
        MountBinding[] mounts =
        [
            new("settings", "/app/settings.json", ResourceMountKind.Configuration, "parameter:settings"),
        ];
        FakeContext oldContext = CreateContext(
            mounts: mounts,
            inputs: CreateInputs("settings", "one"),
            expose: true);
        ResourceEndpoint[] oldEndpoints =
        [
            new("http", "http", 8080, Host: "worker-http.appa.svc"),
            new("http", "http", 8080, IsPublic: true, Host: "old.example.test"),
        ];
        oldContext.State.SetState(
            oldContext.Resource.Id,
            ResourceLifecycle.Running,
            observedEndpoints: oldEndpoints);
        await controller.ReconcileAsync(oldContext);
        KubernetesPlanCompilation oldCompilation = observations.LastCompilation!;

        FakeContext newContext = CreateContext(
            mounts: mounts,
            inputs: CreateInputs("settings", "two"),
            expose: true);
        ResourceEndpoint[] newEndpoints =
        [
            new("http", "http", 8080, Host: "worker-http.appa.svc"),
            new("http", "http", 8080, IsPublic: true, Host: "new.example.test"),
        ];
        newContext.State.SetState(
            newContext.Resource.Id,
            ResourceLifecycle.Running,
            observedEndpoints: newEndpoints);

        KubernetesPlanCompilation staleRefresh;
        using (IDisposable observerMutation = await observations.EnterMutationAsync(oldContext))
        {
            Task newestReconcile = controller.ReconcileAsync(newContext);
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            newestReconcile.IsCompleted.ShouldBeFalse();

            staleRefresh = await controller.RefreshRuntimeEnvironmentAsync(
                oldContext,
                oldCompilation,
                oldEndpoints,
                CancellationToken.None);
            observerMutation.Dispose();
            await newestReconcile.WaitAsync(TimeSpan.FromSeconds(5));
        }

        V1ConfigMap finalConfigMap = api.GetPersisted<V1ConfigMap>();
        finalConfigMap.Data[ResourceEnvironment.Endpoint("http", "PUBLIC_URL")]
            .ShouldBe("http://new.example.test:8080");
        Encoding.UTF8.GetString(finalConfigMap.BinaryData["settings"]).ShouldBe("two");
        string staleRevision = staleRefresh.Objects
            .OfType<V1Deployment>()
            .Single()
            .Spec.Template.Metadata.Annotations[
                KubernetesMetadata.WorkloadRevisionAnnotation];
        api.GetPersisted<V1Deployment>()
            .Spec.Template.Metadata.Annotations[KubernetesMetadata.WorkloadRevisionAnnotation]
            .ShouldNotBe(staleRevision);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Contract: Should refuse a runtime patch from a stale observer compilation")]
    public async Task RefreshRuntimeEnvironmentAsync_AfterNewerWorkloadReconcile_ShouldRefuseStaleCompilation()
    {
        var api = new FakeApi { PersistAppliedObjects = true };
        var observations = new FakeObservations();
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            observations);
        FakeContext oldContext = CreateContext(imageDigestCharacter: 'a', expose: true);
        await controller.ReconcileAsync(oldContext);
        KubernetesPlanCompilation oldCompilation = observations.LastCompilation!;
        FakeContext newContext = CreateContext(imageDigestCharacter: 'b', expose: true);
        await controller.ReconcileAsync(newContext);
        api.ClearRecordings();

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => controller.RefreshRuntimeEnvironmentAsync(
                oldContext,
                oldCompilation,
                [
                    new ResourceEndpoint("http", "http", 8080, Host: "worker-http.appa.svc"),
                    new ResourceEndpoint(
                        "http",
                        "http",
                        8080,
                        IsPublic: true,
                        Host: "old.example.test"),
                ],
                CancellationToken.None));

        exception.Message.ShouldContain("no longer matches the compilation");
        api.JsonPatches.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Contract: Should recover when a committed runtime patch response is lost")]
    public async Task RefreshRuntimeEnvironmentAsync_AfterAmbiguousPatchSuccess_ShouldConvergeOnRetry()
    {
        var api = new FakeApi
        {
            PersistAppliedObjects = true,
            ThrowAfterJsonPatchKind = "Deployment",
        };
        var observations = new FakeObservations();
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            observations);
        FakeContext context = CreateContext(expose: true);
        await controller.ReconcileAsync(context);
        KubernetesPlanCompilation registered = observations.LastCompilation!;
        ResourceEndpoint[] endpoints =
        [
            new("http", "http", 8080, Host: "worker-http.appa.svc"),
            new("http", "http", 8080, IsPublic: true, Host: "worker.example.test"),
        ];
        api.ClearRecordings();

        await Should.ThrowAsync<InvalidOperationException>(
            () => controller.RefreshRuntimeEnvironmentAsync(
                context,
                registered,
                endpoints,
                CancellationToken.None));
        KubernetesPlanCompilation recovered = await controller.RefreshRuntimeEnvironmentAsync(
            context,
            registered,
            endpoints,
            CancellationToken.None);

        api.JsonPatches.Select(static item => item.Kind)
            .ShouldBe(["ConfigMap", "Deployment"]);
        recovered.Objects.OfType<V1ConfigMap>().Single().Data[
            ResourceEnvironment.Endpoint("http", "PUBLIC_URL")]
            .ShouldBe("http://worker.example.test:8080");
        recovered.Objects.OfType<V1Deployment>().Single().Spec.Template.Metadata.Annotations[
            KubernetesMetadata.WorkloadRevisionAnnotation]
            .ShouldBe(api.GetPersisted<V1Deployment>().Spec.Template.Metadata.Annotations[
                KubernetesMetadata.WorkloadRevisionAnnotation]);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Hints: Should warn once per key in each gateway session")]
    public async Task ReconcileAsync_OnRepeatedUnknownHint_ShouldDeduplicatePerSession()
    {
        var warnings = new List<string>();
        var options = new KubernetesGatewayOptions { WarningHandler = warnings.Add };
        var controller = new KubernetesPlanController(
            options,
            new KubernetesPlanCompiler(),
            new FakeApi(),
            new FakeObservations());
        FakeContext context = CreateContext(
            hints: new Dictionary<string, string> { ["example.unsupported"] = "true" });

        controller.BeginSession();
        await controller.ReconcileAsync(context);
        await controller.ReconcileAsync(context);
        warnings.Count.ShouldBe(1);

        controller.BeginSession();
        await controller.ReconcileAsync(context);
        warnings.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should remove the old workload kind after applying its replacement")]
    public async Task ReconcileAsync_OnDeploymentChangedToJob_ShouldDeleteStaleDeployment()
    {
        var api = new FakeApi
        {
            SupportedObjects = [CreateDeployment("worker", "appa@kubernetes")],
        };
        var observations = new FakeObservations();
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            observations);
        FakeContext context = CreateContext(WorkloadKind.Job);

        await controller.ReconcileAsync(context);

        api.Applied.ShouldBe(["ConfigMap", "Secret", "Job"]);
        api.Deleted.ShouldBe(["Deployment"]);
        api.ListedNamespace.ShouldBe("appa");
        api.ListedResourceLabel.ShouldBe("worker");
        observations.Registered.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should refuse a foreign stale object before applying desired state")]
    public async Task ReconcileAsync_OnForeignStaleObject_ShouldRejectBeforeApply()
    {
        var api = new FakeApi
        {
            SupportedObjects = [CreateDeployment("worker", "foreign@gateway")],
        };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => controller.ReconcileAsync(CreateContext(WorkloadKind.Job)));

        exception.Message.ShouldContain("Pass --adopt");
        api.Applied.ShouldBeEmpty();
        api.Deleted.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should refuse an unlabeled foreign StatefulSet claim")]
    public async Task ReconcileAsync_OnUnlabeledForeignStatefulClaim_ShouldRejectBeforeApply()
    {
        V1PersistentVolumeClaim claim = CreateClaim("data-worker-0", "foreign@gateway");
        claim.Metadata.Labels = null;
        var api = new FakeApi { NamespaceClaims = [claim] };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => controller.ReconcileAsync(CreateContext(WorkloadKind.StatefulSet)));

        exception.Message.ShouldContain(KubernetesMetadata.ResourceLabel);
        exception.Message.ShouldContain("Pass --adopt");
        api.Applied.ShouldBeEmpty();
        api.Deleted.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should refuse a retained claim from another resource identity")]
    public async Task ReconcileAsync_OnSameOwnerClaimForAnotherResource_ShouldRejectBeforeApply()
    {
        V1PersistentVolumeClaim claim = CreateClaim("data-worker-0", "appa@kubernetes");
        claim.Metadata.Labels[KubernetesMetadata.ResourceLabel] = "former-worker";
        var api = new FakeApi { NamespaceClaims = [claim] };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => controller.ReconcileAsync(CreateContext(
                WorkloadKind.StatefulSet,
                adopt: true)));

        exception.Message.ShouldContain("Refusing to reuse persistent data");
        api.Applied.ShouldBeEmpty();
        api.Deleted.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should refuse a standalone claim from another resource identity")]
    public void RequirePersistentVolumeClaimIdentity_OnAnotherResource_ShouldRejectAdoption()
    {
        V1PersistentVolumeClaim claim = CreateClaim(
            "shared-data",
            "appa@kubernetes",
            "former-worker");

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => KubernetesPlanController.RequirePersistentVolumeClaimIdentity(
                claim.Metadata,
                "worker",
                adopt: true));

        exception.Message.ShouldContain("Refusing to reuse persistent data");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should refuse a foreign object that wins a create race")]
    public async Task ReconcileAsync_OnForeignCreateRace_ShouldRejectWithoutApply()
    {
        var api = new FakeApi
        {
            CreateConflictKind = "ConfigMap",
            CreateConflictOwner = "foreign@gateway",
        };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => controller.ReconcileAsync(CreateContext()));

        exception.Message.ShouldContain("Pass --adopt");
        api.Applied.ShouldBeEmpty();
        api.Deleted.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should remove obsolete Services while preserving generated claims")]
    public async Task ReconcileAsync_OnRemovedService_ShouldDeleteStaleServiceAndPreserveClaim()
    {
        var api = new FakeApi
        {
            SupportedObjects =
            [
                CreateService("worker-http", "appa@kubernetes"),
                CreateClaim("data-worker-0", "appa@kubernetes"),
            ],
        };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());

        await controller.ReconcileAsync(CreateContext());

        api.Deleted.ShouldBe(["Service"]);
        api.AppliedObjects.OfType<V1PersistentVolumeClaim>().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should replace a Job when its immutable workload revision changes")]
    public async Task ReconcileAsync_OnChangedJobImage_ShouldReplaceJob()
    {
        var api = new FakeApi { PersistAppliedObjects = true };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());

        await controller.ReconcileAsync(CreateContext(WorkloadKind.Job, imageDigestCharacter: 'a'));
        V1Job first = api.AppliedObjects.OfType<V1Job>().Single();
        string firstRevision = first.Metadata.Annotations[
            KubernetesMetadata.WorkloadRevisionAnnotation];
        api.ClearRecordings();

        await controller.ReconcileAsync(CreateContext(WorkloadKind.Job, imageDigestCharacter: 'b'));

        V1Job second = api.AppliedObjects.OfType<V1Job>().Single();
        api.Deleted.ShouldBe(["Job"]);
        api.DeletedObjects.Single().Metadata.Uid.ShouldNotBeNull();
        second.Metadata.Annotations[KubernetesMetadata.WorkloadRevisionAnnotation]
            .ShouldNotBe(firstRevision);
        second.Spec.Template.Spec.Containers.Single().Image
            .ShouldContain(new string('b', 64));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should replace a Job when a patched immutable spec field changes")]
    public async Task ReconcileAsync_OnChangedJobSpecPatch_ShouldReplaceJob()
    {
        var api = new FakeApi { PersistAppliedObjects = true };
        var first = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());
        await first.ReconcileAsync(CreateContext(WorkloadKind.Job));
        string firstRevision = api.AppliedObjects.OfType<V1Job>().Single()
            .Metadata.Annotations[KubernetesMetadata.JobSpecRevisionAnnotation];
        api.ClearRecordings();
        var changedOptions = new KubernetesGatewayOptions();
        changedOptions.Patch<V1Job>("worker", job => job.Spec.CompletionMode = "Indexed");
        var changed = new KubernetesPlanController(
            changedOptions,
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());

        await changed.ReconcileAsync(CreateContext(WorkloadKind.Job));

        V1Job replacement = api.AppliedObjects.OfType<V1Job>().Single();
        api.Deleted.ShouldBe(["Job"]);
        replacement.Spec.CompletionMode.ShouldBe("Indexed");
        replacement.Metadata.Annotations[KubernetesMetadata.JobSpecRevisionAnnotation]
            .ShouldNotBe(firstRevision);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should roll pods when resolved mount inputs change without changing plan drift")]
    public async Task ReconcileAsync_OnChangedResolvedInput_ShouldAdvanceRuntimeRevision()
    {
        var api = new FakeApi { PersistAppliedObjects = true };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());
        MountBinding[] mounts =
        [
            new("settings", "/app/settings.json", ResourceMountKind.Configuration, "parameter:settings"),
        ];
        ResourceInputs firstInputs = CreateInputs("settings", "one");
        ResourceInputs secondInputs = CreateInputs("settings", "two");

        await controller.ReconcileAsync(CreateContext(mounts: mounts, inputs: firstInputs));
        V1Deployment first = api.AppliedObjects.OfType<V1Deployment>().Single();
        string firstPlanHash = first.Metadata.Annotations[KubernetesMetadata.PlanHashAnnotation];
        string firstRevision = first.Spec.Template.Metadata.Annotations[
            KubernetesMetadata.RuntimeInputRevisionAnnotation];
        api.ClearRecordings();

        await controller.ReconcileAsync(CreateContext(mounts: mounts, inputs: secondInputs));

        V1Deployment second = api.AppliedObjects.OfType<V1Deployment>().Single();
        second.Metadata.Annotations[KubernetesMetadata.PlanHashAnnotation]
            .ShouldBe(firstPlanHash);
        second.Spec.Template.Metadata.Annotations[
            KubernetesMetadata.RuntimeInputRevisionAnnotation]
            .ShouldNotBe(firstRevision);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should rotate the projected bootstrap token without rolling pods")]
    public async Task ReconcileAsync_OnRotatedBootstrapCredential_ShouldPreserveRuntimeRevision()
    {
        var api = new FakeApi { PersistAppliedObjects = true };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());
        var firstInputs = new ResourceInputs(
            new Dictionary<string, ResourceMountInput>(),
            Encoding.UTF8.GetBytes("first"));
        var secondInputs = new ResourceInputs(
            new Dictionary<string, ResourceMountInput>(),
            Encoding.UTF8.GetBytes("second"));

        await controller.ReconcileAsync(CreateContext(inputs: firstInputs));
        V1Deployment first = api.AppliedObjects.OfType<V1Deployment>().Single();
        string firstRevision = first.Spec.Template.Metadata.Annotations[
            KubernetesMetadata.RuntimeInputRevisionAnnotation];
        api.ClearRecordings();

        await controller.ReconcileAsync(CreateContext(inputs: secondInputs));

        V1Deployment second = api.AppliedObjects.OfType<V1Deployment>().Single();
        second.Spec.Template.Metadata.Annotations[
            KubernetesMetadata.RuntimeInputRevisionAnnotation]
            .ShouldBe(firstRevision);
        api.AppliedObjects.OfType<V1Secret>().Single().Data["bootstrap-token"]
            .ShouldBe(Encoding.UTF8.GetBytes("second"));
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should reject an immutable StatefulSet claim-template change")]
    public async Task ReconcileAsync_OnChangedStatefulClaimSize_ShouldRejectInPlaceUpdate()
    {
        var api = new FakeApi { PersistAppliedObjects = true };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());
        await controller.ReconcileAsync(CreateContext(
            WorkloadKind.StatefulSet,
            volumeSize: "1Gi"));
        api.ClearRecordings();

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => controller.ReconcileAsync(CreateContext(
                WorkloadKind.StatefulSet,
                volumeSize: "2Gi")));

        exception.Message.ShouldContain("volumeClaimTemplates");
        api.Applied.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should reject an incompatible retained StatefulSet claim")]
    public async Task ReconcileAsync_OnRetainedStatefulClaimSmallerThanTemplate_ShouldReject()
    {
        V1PersistentVolumeClaim claim = CreateClaim("data-worker-0", "appa@kubernetes");
        var api = new FakeApi
        {
            SupportedObjects = [claim],
            NamespaceClaims = [claim],
        };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => controller.ReconcileAsync(CreateContext(
                WorkloadKind.StatefulSet,
                volumeSize: "2Gi")));

        exception.Message.ShouldContain("different storage specification");
        api.Applied.ShouldBeEmpty();
        api.Deleted.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should accept a separately expanded retained StatefulSet claim")]
    public async Task ReconcileAsync_OnExpandedRetainedStatefulClaim_ShouldPreserveIt()
    {
        V1PersistentVolumeClaim claim = CreateClaim("data-worker-0", "appa@kubernetes");
        claim.Spec.Resources.Requests["storage"] = new ResourceQuantity("2Gi");
        var api = new FakeApi { NamespaceClaims = [claim] };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());

        await controller.ReconcileAsync(CreateContext(WorkloadKind.StatefulSet));

        api.Deleted.ShouldBeEmpty();
        api.AppliedObjects.OfType<V1PersistentVolumeClaim>().ShouldBeEmpty();
        api.Applied.ShouldContain("StatefulSet");
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should preserve immutable claims when a StatefulSet plan changes")]
    public async Task ReconcileAsync_OnChangedStatefulPlan_ShouldUpdateMutableControllerFields()
    {
        var api = new FakeApi { PersistAppliedObjects = true };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());
        await controller.ReconcileAsync(CreateContext(
            WorkloadKind.StatefulSet,
            imageDigestCharacter: 'a'));
        V1StatefulSet first = api.AppliedObjects.OfType<V1StatefulSet>().Single();
        string firstClaimPlanHash = first.Spec.VolumeClaimTemplates.Single()
            .Metadata.Annotations[KubernetesMetadata.PlanHashAnnotation];
        api.ClearRecordings();

        await controller.ReconcileAsync(CreateContext(
            WorkloadKind.StatefulSet,
            imageDigestCharacter: 'b',
            replicas: 2));

        V1StatefulSet second = api.AppliedObjects.OfType<V1StatefulSet>().Single();
        second.Spec.Replicas.ShouldBe(2);
        second.Spec.Template.Spec.Containers.Single().Image
            .ShouldContain(new string('b', 64));
        second.Spec.VolumeClaimTemplates.Single().Metadata.Annotations[
            KubernetesMetadata.PlanHashAnnotation].ShouldBe(firstClaimPlanHash);
        api.Deleted.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should reject a changed immutable StatefulSet governing Service")]
    public async Task ReconcileAsync_OnChangedStatefulServiceName_ShouldRejectBeforeApply()
    {
        var api = new FakeApi { PersistAppliedObjects = true };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());
        await controller.ReconcileAsync(CreateContext(WorkloadKind.StatefulSet));
        api.ClearRecordings();

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => controller.ReconcileAsync(CreateContext(
                WorkloadKind.StatefulSet,
                governingServiceName: "worker-peer")));

        exception.Message.ShouldContain("spec.serviceName");
        api.Applied.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should recreate an adopted StatefulSet while preserving claims")]
    public async Task ReconcileAsync_OnForeignStatefulSetWithClaims_ShouldRecreateControllerOnly()
    {
        var generatedClaim = CreateClaim("data-worker-0", "foreign@gateway");
        generatedClaim.Metadata.Labels.Remove(KubernetesMetadata.ManagedByLabel);
        var api = new FakeApi
        {
            PersistAppliedObjects = true,
            NamespaceClaims = [generatedClaim],
        };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());
        await controller.ReconcileAsync(CreateContext(
            WorkloadKind.StatefulSet,
            owner: "foreign@gateway"));
        V1PersistentVolumeClaim existingTemplate = api.GetPersisted<V1StatefulSet>()
            .Spec.VolumeClaimTemplates.Single();
        existingTemplate.Spec.StorageClassName = "retained-class";
        generatedClaim.Spec.StorageClassName = "retained-class";
        existingTemplate.Metadata.Labels = null;
        existingTemplate.Metadata.Annotations = null;
        api.ClearRecordings();

        await controller.ReconcileAsync(CreateContext(
            WorkloadKind.StatefulSet,
            adopt: true));

        V1StatefulSet statefulSet = api.AppliedObjects.OfType<V1StatefulSet>().Single();
        api.Deleted.ShouldBe(["StatefulSet"]);
        statefulSet.Metadata.Annotations[KubernetesMetadata.OwnerAnnotation]
            .ShouldBe("appa@kubernetes");
        statefulSet.Spec.VolumeClaimTemplates.Single().Metadata.Annotations[
            KubernetesMetadata.OwnerAnnotation].ShouldBe("appa@kubernetes");
        statefulSet.Spec.VolumeClaimTemplates.Single().Spec.StorageClassName
            .ShouldBe("retained-class");
        V1PersistentVolumeClaim adoptedClaim = api.AppliedObjects
            .OfType<V1PersistentVolumeClaim>().Single();
        adoptedClaim.Metadata.Annotations[KubernetesMetadata.OwnerAnnotation]
            .ShouldBe("appa@kubernetes");
        api.Deleted.ShouldBe(["StatefulSet"]);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should refuse unsafe StatefulSet owner adoption")]
    public async Task ReconcileAsync_OnAdoptedStatefulSetDeletingClaims_ShouldRefuseRecreation()
    {
        var api = new FakeApi { PersistAppliedObjects = true };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());
        await controller.ReconcileAsync(CreateContext(
            WorkloadKind.StatefulSet,
            owner: "foreign@gateway"));
        api.GetPersisted<V1StatefulSet>()
            .Spec.PersistentVolumeClaimRetentionPolicy =
                new V1StatefulSetPersistentVolumeClaimRetentionPolicy(
                    whenDeleted: "Delete",
                    whenScaled: "Retain");
        api.ClearRecordings();

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => controller.ReconcileAsync(CreateContext(
                WorkloadKind.StatefulSet,
                adopt: true)));

        exception.Message.ShouldContain("whenDeleted is not Retain");
        api.Applied.ShouldBeEmpty();
        api.Deleted.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should prune objects for resources removed from the application model")]
    public async Task PruneRemovedResourcesAsync_OnRemovedResource_ShouldStopWorkloadsAndRetainClaims()
    {
        var api = new FakeApi
        {
            DelayedDeleteKind = V1Deployment.KubeKind,
            SupportedObjects =
            [
                CreateDeployment("former-worker", "appa@kubernetes", "former-worker"),
                CreateService("former-worker-http", "appa@kubernetes", "former-worker"),
                CreateClaim("former-data", "appa@kubernetes", "former-worker"),
            ],
        };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());

        await controller.PruneRemovedResourcesAsync(CreateContext().Model, "appa");

        api.Deleted.ShouldBe(["Deployment", "Service"]);
        api.DeletedObjects.OfType<V1PersistentVolumeClaim>().ShouldBeEmpty();
        api.ListedResourceLabel.ShouldBeNull();
        api.DeletionReadCount.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should refuse pruning a StatefulSet that deletes retained claims")]
    public async Task PruneRemovedResourcesAsync_OnStatefulSetDeletingClaims_ShouldRefuse()
    {
        V1StatefulSet statefulSet = CreateStatefulSet(
            "former-worker",
            "appa@kubernetes",
            "former-worker");
        statefulSet.Spec.PersistentVolumeClaimRetentionPolicy =
            new V1StatefulSetPersistentVolumeClaimRetentionPolicy(
                whenDeleted: "Delete",
                whenScaled: "Retain");
        var api = new FakeApi { SupportedObjects = [statefulSet] };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => controller.PruneRemovedResourcesAsync(CreateContext().Model, "appa"));

        exception.Message.ShouldContain("whenDeleted is not Retain");
        api.Deleted.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should refuse pruning a StatefulSet whose claims retain controller owner references")]
    public async Task PruneRemovedResourcesAsync_OnStatefulSetOwnedClaim_ShouldRefuse()
    {
        V1StatefulSet statefulSet = CreateStatefulSet(
            "former-worker",
            "appa@kubernetes",
            "former-worker");
        V1PersistentVolumeClaim claim = CreateClaim(
            "data-former-worker-0",
            "appa@kubernetes",
            "former-worker");
        claim.Metadata.OwnerReferences =
        [
            new V1OwnerReference
            {
                ApiVersion = $"{V1StatefulSet.KubeGroup}/{V1StatefulSet.KubeApiVersion}",
                Kind = V1StatefulSet.KubeKind,
                Name = statefulSet.Metadata.Name,
                Uid = statefulSet.Metadata.Uid,
            },
        ];
        var api = new FakeApi { SupportedObjects = [claim, statefulSet] };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => controller.PruneRemovedResourcesAsync(CreateContext().Model, "appa"));

        exception.Message.ShouldContain("ownerReference");
        exception.Message.ShouldContain(claim.Metadata.Name);
        api.Deleted.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should delete the compiled set in reverse order")]
    public async Task DeleteAsync_OnV1Plan_ShouldDeleteInReverseOrder()
    {
        var api = new FakeApi { ExistingOwner = "appa@kubernetes" };
        var observations = new FakeObservations();
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            observations);
        FakeContext context = CreateContext();

        await controller.DeleteAsync(context);

        api.Deleted.ShouldBe(["Deployment", "Secret", "ConfigMap"]);
        observations.TeardownBegan.ShouldBeTrue();
        observations.Unregistered.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should await object disappearance before reporting teardown success")]
    public async Task DeleteAsync_OnTerminatingObject_ShouldWaitBeforeUnregistering()
    {
        var api = new FakeApi
        {
            ExistingOwner = "appa@kubernetes",
            DelayedDeleteKind = V1Deployment.KubeKind,
        };
        var observations = new FakeObservations();
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            observations);

        await controller.DeleteAsync(CreateContext());

        api.DeletionReadCount.ShouldBe(2);
        observations.Unregistered.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should continue best-effort teardown after an object delete fails")]
    public async Task DeleteAsync_OnIntermediateFailure_ShouldAttemptEveryObject()
    {
        var api = new FakeApi
        {
            ExistingOwner = "appa@kubernetes",
            DeleteFailureKind = "Secret",
        };
        var observations = new FakeObservations();
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            observations);

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => controller.DeleteAsync(CreateContext()));

        exception.Message.ShouldContain("Secret");
        api.Deleted.ShouldBe(["Deployment", "Secret", "ConfigMap"]);
        observations.Stopped.ShouldBeTrue();
        observations.Unregistered.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should stop observation without deleting objects")]
    public async Task StopAsync_OnRunningResource_ShouldPreserveCompiledObjects()
    {
        var api = new FakeApi();
        var observations = new FakeObservations();
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            observations);

        await controller.StopAsync(CreateContext());

        api.Deleted.ShouldBeEmpty();
        observations.Stopped.ShouldBeTrue();
        observations.Unregistered.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should refuse a foreign object without adopt")]
    public async Task ReconcileAsync_OnForeignOwner_ShouldRefuseObject()
    {
        var api = new FakeApi { ExistingOwner = "foreign@gateway" };
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            new FakeObservations());

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => controller.ReconcileAsync(CreateContext()));

        exception.Message.ShouldContain("Pass --adopt");
        api.Applied.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Controller: Should not mark teardown after a foreign delete failure")]
    public async Task DeleteAsync_OnForeignOwner_ShouldDetachWithoutMarkingTeardown()
    {
        var api = new FakeApi { ExistingOwner = "foreign@gateway" };
        var observations = new FakeObservations();
        var controller = new KubernetesPlanController(
            new KubernetesGatewayOptions(),
            new KubernetesPlanCompiler(),
            api,
            observations);

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => controller.DeleteAsync(CreateContext()));

        exception.Message.ShouldContain("Pass --adopt");
        api.Deleted.ShouldBeEmpty();
        observations.Stopped.ShouldBeTrue();
        observations.Unregistered.ShouldBeFalse();
    }

    private static FakeContext CreateContext(
        WorkloadKind workloadKind = WorkloadKind.Deployment,
        char imageDigestCharacter = 'a',
        IReadOnlyList<MountBinding>? mounts = null,
        ResourceInputs? inputs = null,
        string volumeSize = "1Gi",
        int replicas = 1,
        string governingServiceName = "worker-headless",
        string owner = "appa@kubernetes",
        bool adopt = false,
        IReadOnlyDictionary<string, string>? hints = null,
        bool expose = false)
    {
        var resource = new FakeResource("worker");
        IReadOnlyList<MountBinding> effectiveMounts = mounts
            ?? (workloadKind is WorkloadKind.StatefulSet
                ? [new MountBinding("data", "/data", ResourceMountKind.Volume, null)]
                : []);
        IReadOnlyList<VolumeSpec> volumes = workloadKind is WorkloadKind.StatefulSet
            ? [new VolumeSpec("data", ResourceMountKind.Volume, volumeSize, true)]
            : [];
        IReadOnlyList<PortBinding> ports = expose
            ? [new PortBinding("http", 8080, "tcp")]
            : [];
        IReadOnlyList<ServiceSpec> services = (workloadKind, expose) switch
        {
            (WorkloadKind.StatefulSet, true) =>
            [
                new ServiceSpec(governingServiceName, null, null, "tcp", true, true),
                new ServiceSpec("worker-http", "http", 8080, "tcp", false, false),
            ],
            (WorkloadKind.StatefulSet, false) =>
                [new ServiceSpec(governingServiceName, null, null, "tcp", true, true)],
            (_, true) => [new ServiceSpec("worker-http", "http", 8080, "tcp", false, false)],
            _ => [],
        };
        IReadOnlyList<ExposureSpec> exposures = expose
            ? [new ExposureSpec("worker-public", "http", "worker-http", "http", "tcp", 8080)]
            : [];
        var plan = new ResourcePlan(
            ResourcePlan.CurrentSchema,
            resource.Name,
            "Worker",
            new WorkloadSpec(
                workloadKind,
                replicas,
                workloadKind is WorkloadKind.StatefulSet,
                ReadinessGate.For(workloadKind),
                30),
            new ContainerSpec(
                "worker",
                ArtifactRef.Self,
                ports,
                effectiveMounts,
                new Dictionary<string, string>(),
                []),
            volumes,
            services,
            exposures,
            hints ?? new Dictionary<string, string>());
        IContainerImageArtifact artifact = ContainerImageArtifacts.Create(
            resource.Id,
            $"registry.example/worker@sha256:{new string(imageDigestCharacter, 64)}");
        return new FakeContext(
            resource,
            plan,
            artifact,
            inputs ?? ResourceInputs.Empty,
            owner,
            adopt);
    }

    private static ResourceInputs CreateInputs(string name, string value) => new(
        new Dictionary<string, ResourceMountInput>
        {
            [name] = ResourceMountInput.Resolved(
                $"parameter:{name}",
                Encoding.UTF8.GetBytes(value)),
        },
        ReadOnlyMemory<byte>.Empty);

    private static V1Deployment CreateDeployment(
        string name,
        string owner,
        string resource = "worker") => new()
        {
            ApiVersion = $"{V1Deployment.KubeGroup}/{V1Deployment.KubeApiVersion}",
            Kind = V1Deployment.KubeKind,
            Metadata = CreateMetadata(name, owner, resource),
        };

    private static V1Service CreateService(
        string name,
        string owner,
        string resource = "worker") => new()
        {
            ApiVersion = V1Service.KubeApiVersion,
            Kind = V1Service.KubeKind,
            Metadata = CreateMetadata(name, owner, resource),
        };

    private static V1PersistentVolumeClaim CreateClaim(
        string name,
        string owner,
        string resource = "worker") => new()
        {
            ApiVersion = V1PersistentVolumeClaim.KubeApiVersion,
            Kind = V1PersistentVolumeClaim.KubeKind,
            Metadata = CreateMetadata(name, owner, resource),
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes = ["ReadWriteOnce"],
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity>
                    {
                        ["storage"] = new ResourceQuantity("1Gi"),
                    },
                },
            },
        };

    private static V1StatefulSet CreateStatefulSet(
        string name,
        string owner,
        string resource = "worker") => new()
        {
            ApiVersion = $"{V1StatefulSet.KubeGroup}/{V1StatefulSet.KubeApiVersion}",
            Kind = V1StatefulSet.KubeKind,
            Metadata = CreateMetadata(name, owner, resource),
            Spec = new V1StatefulSetSpec
            {
                Selector = new V1LabelSelector(),
                ServiceName = $"{name}-headless",
                Template = new V1PodTemplateSpec(),
                VolumeClaimTemplates = [CreateClaim("data", owner, resource)],
            },
        };

    private static V1ObjectMeta CreateMetadata(
        string name,
        string owner,
        string resource = "worker") => new()
        {
            Name = name,
            NamespaceProperty = "appa",
            Uid = $"{name}-uid",
            ResourceVersion = "1",
            Labels = new Dictionary<string, string>
            {
                [KubernetesMetadata.ManagedByLabel] = KubernetesMetadata.ManagedByValue,
                [KubernetesMetadata.ResourceLabel] = resource,
            },
            Annotations = new Dictionary<string, string>
            {
                [KubernetesMetadata.OwnerAnnotation] = owner,
            },
        };

    private sealed class FakeApi : IKubernetesResourceApi
    {
        private readonly Dictionary<string, IKubernetesObject<V1ObjectMeta>> _objects =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
        private int _resourceVersion;
        private int _jsonPatchFailureThrown;
        private string? _pendingDeletion;

        public List<string> Applied { get; } = [];
        public List<IKubernetesObject<V1ObjectMeta>> AppliedObjects { get; } = [];
        public List<(string Kind, string Patch)> JsonPatches { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<IKubernetesObject<V1ObjectMeta>> DeletedObjects { get; } = [];
        public string? ExistingOwner { get; init; }
        public string? CreateConflictKind { get; init; }
        public string? CreateConflictOwner { get; init; }
        public bool PersistAppliedObjects { get; init; }
        public string? DeleteFailureKind { get; init; }
        public string? ThrowAfterJsonPatchKind { get; init; }
        public string? DelayedDeleteKind { get; init; }
        public int DeletionReadCount { get; private set; }
        public IReadOnlyList<IKubernetesObject<V1ObjectMeta>> SupportedObjects { get; init; } = [];
        public IReadOnlyList<V1PersistentVolumeClaim> NamespaceClaims { get; init; } = [];
        public string? ListedNamespace { get; private set; }
        public string? ListedResourceLabel { get; private set; }

        public Task<bool> TryCreateAsync(
            IKubernetesObject<V1ObjectMeta> resource,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(resource.Kind, CreateConflictKind, StringComparison.Ordinal))
            {
                _objects[Key(resource)] = CreateExisting(
                    resource,
                    CreateConflictOwner ?? "foreign@gateway");
                return Task.FromResult(false);
            }

            RecordApplied(resource);
            return Task.FromResult(true);
        }

        public Task ApplyAsync(IKubernetesObject<V1ObjectMeta> resource, bool force, CancellationToken cancellationToken = default)
        {
            RecordApplied(resource);
            return Task.CompletedTask;
        }

        public Task JsonPatchAsync(
            IKubernetesObject<V1ObjectMeta> resource,
            string patch,
            CancellationToken cancellationToken = default)
        {
            JsonPatches.Add((resource.Kind, patch));
            if (!PersistAppliedObjects
                || !_objects.TryGetValue(Key(resource), out IKubernetesObject<V1ObjectMeta>? current))
            {
                return Task.CompletedTask;
            }

            using JsonDocument document = JsonDocument.Parse(patch);
            foreach (JsonElement operation in document.RootElement.EnumerateArray())
            {
                string name = operation.GetProperty("op").GetString()!;
                string path = operation.GetProperty("path").GetString()!;
                string? value = operation.TryGetProperty("value", out JsonElement element)
                    ? element.GetString()
                    : null;
                ApplyJsonPatchOperation(current, name, path, value);
            }

            current.Metadata.ResourceVersion = (++_resourceVersion).ToString();
            if (string.Equals(resource.Kind, ThrowAfterJsonPatchKind, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _jsonPatchFailureThrown, 1) == 0)
            {
                throw new InvalidOperationException(
                    $"Synthetic lost {resource.Kind} JSON Patch response.");
            }

            return Task.CompletedTask;
        }

        private static void ApplyJsonPatchOperation(
            IKubernetesObject<V1ObjectMeta> resource,
            string operation,
            string path,
            string? value)
        {
            if (string.Equals(operation, "test", StringComparison.Ordinal))
            {
                if (!string.Equals(
                        path,
                        "/metadata/resourceVersion",
                        StringComparison.Ordinal)
                    || !string.Equals(
                        resource.Metadata.ResourceVersion,
                        value,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Synthetic JSON Patch test failed.");
                }

                return;
            }

            const string dataPrefix = "/data/";
            if (resource is V1ConfigMap configMap
                && path.StartsWith(dataPrefix, StringComparison.Ordinal))
            {
                string key = UnescapeJsonPointer(path[dataPrefix.Length..]);
                configMap.Data ??= new Dictionary<string, string>(StringComparer.Ordinal);
                if (string.Equals(operation, "remove", StringComparison.Ordinal))
                {
                    configMap.Data.Remove(key);
                }
                else
                {
                    configMap.Data[key] = value!;
                }

                return;
            }

            const string metadataPrefix = "/metadata/annotations/";
            if (path.StartsWith(metadataPrefix, StringComparison.Ordinal))
            {
                resource.Metadata.Annotations ??=
                    new Dictionary<string, string>(StringComparer.Ordinal);
                resource.Metadata.Annotations[
                    UnescapeJsonPointer(path[metadataPrefix.Length..])] = value!;
                return;
            }

            const string templatePrefix = "/spec/template/metadata/annotations/";
            if (path.StartsWith(templatePrefix, StringComparison.Ordinal))
            {
                V1PodTemplateSpec template = resource switch
                {
                    V1Deployment deployment => deployment.Spec.Template,
                    V1StatefulSet statefulSet => statefulSet.Spec.Template,
                    V1DaemonSet daemonSet => daemonSet.Spec.Template,
                    V1Job job => job.Spec.Template,
                    _ => throw new InvalidOperationException(
                        $"Unsupported fake workload kind '{resource.Kind}'."),
                };
                template.Metadata.Annotations ??=
                    new Dictionary<string, string>(StringComparer.Ordinal);
                template.Metadata.Annotations[
                    UnescapeJsonPointer(path[templatePrefix.Length..])] = value!;
                return;
            }

            throw new InvalidOperationException(
                $"Unsupported synthetic JSON Patch path '{path}'.");
        }

        private static string UnescapeJsonPointer(string value) =>
            value.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);

        private void RecordApplied(IKubernetesObject<V1ObjectMeta> resource)
        {
            _deleted.Remove(Key(resource));
            Applied.Add(resource.Kind);
            AppliedObjects.Add(resource);
            if (PersistAppliedObjects)
            {
                resource.Metadata.Uid ??= $"{resource.Kind}-{resource.Metadata.Name}-uid";
                resource.Metadata.ResourceVersion = (++_resourceVersion).ToString();
                _objects[Key(resource)] = CloneResource(resource);
            }
        }

        private static IKubernetesObject<V1ObjectMeta> CloneResource(
            IKubernetesObject<V1ObjectMeta> resource) => resource switch
            {
                V1ConfigMap value => KubernetesJson.Deserialize<V1ConfigMap>(
                    KubernetesJson.Serialize(value)),
                V1Secret value => KubernetesJson.Deserialize<V1Secret>(
                    KubernetesJson.Serialize(value)),
                V1Service value => KubernetesJson.Deserialize<V1Service>(
                    KubernetesJson.Serialize(value)),
                V1PersistentVolumeClaim value =>
                    KubernetesJson.Deserialize<V1PersistentVolumeClaim>(
                        KubernetesJson.Serialize(value)),
                V1Deployment value => KubernetesJson.Deserialize<V1Deployment>(
                    KubernetesJson.Serialize(value)),
                V1StatefulSet value => KubernetesJson.Deserialize<V1StatefulSet>(
                    KubernetesJson.Serialize(value)),
                V1DaemonSet value => KubernetesJson.Deserialize<V1DaemonSet>(
                    KubernetesJson.Serialize(value)),
                V1Job value => KubernetesJson.Deserialize<V1Job>(KubernetesJson.Serialize(value)),
                _ => throw new InvalidOperationException(
                    $"Unsupported fake clone kind '{resource.Kind}'."),
            };

        public Task DeleteAsync(IKubernetesObject<V1ObjectMeta> resource, CancellationToken cancellationToken = default)
        {
            Deleted.Add(resource.Kind);
            DeletedObjects.Add(resource);
            if (string.Equals(resource.Kind, DeleteFailureKind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Synthetic {resource.Kind} deletion failure.");
            }

            if (string.Equals(resource.Kind, DelayedDeleteKind, StringComparison.Ordinal))
            {
                _pendingDeletion = Key(resource);
                _objects[_pendingDeletion] = resource;
                return Task.CompletedTask;
            }

            string key = Key(resource);
            _objects.Remove(key);
            _deleted.Add(key);
            return Task.CompletedTask;
        }

        public Task<IKubernetesObject<V1ObjectMeta>?> ReadAsync(IKubernetesObject<V1ObjectMeta> resource, CancellationToken cancellationToken = default)
        {
            string key = Key(resource);
            if (string.Equals(key, _pendingDeletion, StringComparison.Ordinal))
            {
                DeletionReadCount++;
                if (DeletionReadCount == 1)
                {
                    return Task.FromResult<IKubernetesObject<V1ObjectMeta>?>(_objects[key]);
                }

                _objects.Remove(key);
                _pendingDeletion = null;
                _deleted.Add(key);
                return Task.FromResult<IKubernetesObject<V1ObjectMeta>?>(null);
            }

            IKubernetesObject<V1ObjectMeta>? existing;
            if (_deleted.Contains(key))
            {
                existing = null;
            }
            else if (ExistingOwner is not null)
            {
                existing = CreateExisting(resource, ExistingOwner);
            }
            else
            {
                _objects.TryGetValue(Key(resource), out existing);
            }

            return Task.FromResult(existing);
        }

        public void ClearRecordings()
        {
            Applied.Clear();
            AppliedObjects.Clear();
            JsonPatches.Clear();
            Deleted.Clear();
            DeletedObjects.Clear();
        }

        public T GetPersisted<T>()
            where T : class, IKubernetesObject<V1ObjectMeta> =>
            _objects.Values.OfType<T>().Single();

        public Task<IReadOnlyList<IKubernetesObject<V1ObjectMeta>>> ListSupportedObjectsAsync(
            string namespaceName,
            string? resourceLabel,
            CancellationToken cancellationToken = default)
        {
            ListedNamespace = namespaceName;
            ListedResourceLabel = resourceLabel;
            return Task.FromResult(SupportedObjects);
        }

        public Task<IReadOnlyList<V1PersistentVolumeClaim>> ListPersistentVolumeClaimsAsync(
            string namespaceName,
            CancellationToken cancellationToken = default) => Task.FromResult(NamespaceClaims);

        public Task<V1PodList> ListPodsAsync(string namespaceName, string? labelSelector = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new V1PodList());
        public Task<V1Endpoints?> ReadEndpointsAsync(
            string namespaceName,
            string serviceName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<V1Endpoints?>(null);
        public async IAsyncEnumerable<V1Pod> WatchPodsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        private static string Key(IKubernetesObject<V1ObjectMeta> resource) =>
            $"{resource.ApiVersion}/{resource.Kind}/{resource.Metadata.Name}";

        private static IKubernetesObject<V1ObjectMeta> CreateExisting(
            IKubernetesObject<V1ObjectMeta> desired,
            string owner)
        {
            IKubernetesObject<V1ObjectMeta> existing = desired switch
            {
                V1ConfigMap => new V1ConfigMap(),
                V1Secret => new V1Secret(),
                V1Service => new V1Service(),
                V1PersistentVolumeClaim => new V1PersistentVolumeClaim(),
                V1Deployment => new V1Deployment(),
                V1StatefulSet => new V1StatefulSet(),
                V1DaemonSet => new V1DaemonSet(),
                V1Job => new V1Job(),
                _ => throw new InvalidOperationException($"Unsupported fake kind '{desired.Kind}'."),
            };
            existing.ApiVersion = desired.ApiVersion;
            existing.Kind = desired.Kind;
            existing.Metadata = new V1ObjectMeta
            {
                Name = desired.Metadata.Name,
                NamespaceProperty = desired.Metadata.NamespaceProperty,
                Uid = $"{desired.Kind}-{desired.Metadata.Name}-uid",
                ResourceVersion = "1",
                Annotations = new Dictionary<string, string>
                {
                    [KubernetesMetadata.OwnerAnnotation] = owner,
                },
            };
            return existing;
        }
    }

    private sealed class FakeObservations : IKubernetesObservationRegistry
    {
        private readonly SemaphoreSlim _mutation = new(1, 1);
        public bool TeardownBegan { get; private set; }
        public bool Registered { get; private set; }
        public int RegisterCount { get; private set; }
        public bool Stopped { get; private set; }
        public bool Unregistered { get; private set; }
        public KubernetesPlanCompilation? LastCompilation { get; private set; }
        public async Task<IDisposable> EnterMutationAsync(
            IResourceControlContext context,
            CancellationToken cancellationToken = default)
        {
            await _mutation.WaitAsync(cancellationToken);
            return new MutationLease(_mutation);
        }
        public void BeginTeardown(IResourceControlContext context) => TeardownBegan = true;
        public void Register(IResourceControlContext context, KubernetesPlanCompilation compilation)
        {
            Registered = true;
            RegisterCount++;
            LastCompilation = compilation;
        }
        public void Stop(IResourceControlContext context) => Stopped = true;
        public void Unregister(IResourceControlContext context) => Unregistered = true;

        private sealed class MutationLease : IDisposable
        {
            private SemaphoreSlim? _gate;

            public MutationLease(SemaphoreSlim gate) => _gate = gate;

            public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }

    private sealed class FakeContext : IResourceControlContext
    {
        private readonly IContainerImageArtifact _artifact;
        public FakeContext(
            FakeResource resource,
            ResourcePlan plan,
            IContainerImageArtifact artifact,
            ResourceInputs inputs,
            string owner,
            bool adopt)
        {
            Resource = resource;
            Plan = plan;
            _artifact = artifact;
            Inputs = inputs;
            Model = new FakeModel(plan, resource, owner, adopt);
        }

        public IApplicationResourceDescriptor Descriptor => null!;
        public IApplicationResource Resource { get; }
        public ResourcePlan Plan { get; }
        public IApplicationModel Model { get; }
        public IApplicationResourceStateManager State { get; } = new InMemoryResourceStateManager();
        public IReadOnlyList<IApplicationResource> Dependencies => [];
        public ResourceInputs Inputs { get; }
        public IReadOnlyList<ResourceDependencyObservation> ObservedDependencies => [];
        public T GetArtifact<T>() where T : class, IResourceArtifact => (T)_artifact;
    }

    private sealed class FakeResource : IApplicationResource
    {
        private readonly ResourceId _id = ResourceId.New();
        public FakeResource(string name) => Name = name;
        public ResourceName Name { get; }
        public ResourceId Id => _id;
    }

    private sealed class FakeModel : IApplicationModel
    {
        private readonly string _owner;
        private readonly bool _adopt;

        public FakeModel(
            ResourcePlan plan,
            IApplicationResource resource,
            string owner,
            bool adopt)
        {
            Plans = [plan];
            Resources = [resource];
            _owner = owner;
            _adopt = adopt;
        }

        public ApplicationName Name => "appa";
        public IApplicationEnvironment Environment => new FakeEnvironment();
        public GatewayRunMode RunMode => GatewayRunMode.Run;
        public ResourceName GatewayIdentity => "kubernetes";
        public string Owner => _owner;
        public bool Adopt => _adopt;
        public bool RestartOrphans => false;
        public IReadOnlyList<IApplicationResourceDescriptor> Descriptors => [];
        public IReadOnlyList<IApplicationResource> Resources { get; }
        public IReadOnlyList<ResourceManifest> Manifests => [];
        public IReadOnlyList<ResourcePlan> Plans { get; }
    }

    private sealed class FakeEnvironment : IApplicationEnvironment
    {
        public EnvironmentName Name => "Local";
        public bool IsLocal => true;
        public bool IsDevelopment => false;
    }
}
