using System;
using System.IO;
using System.Reflection;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public sealed class KubernetesGatewayCommandLineTests
{
    [Theory(DisplayName = "Cohesion Test [Kubernetes] - CLI: Every valued switch accepts separated and equals forms")]
    [InlineData("--context", "test", "ContextName")]
    [InlineData("--kubeconfig", "test", "KubeConfigPath")]
    [InlineData("--cohesion-system-namespace", "test", "SystemNamespace")]
    [InlineData("--cohesion-system-image", KubernetesSystemInstallationTests.Image, "SystemImage")]
    [InlineData("--cohesion-system-service-account", "test", "SystemServiceAccount")]
    [InlineData("--cohesion-system-storage", "2Gi", "SystemStorageSize")]
    [InlineData("--control-plane-host", "gateway.example.test", "SystemIngressHost")]
    [InlineData("--control-plane-expose", "LoadBalancer", "SystemExposure")]
    public void Apply_OnValuedSwitch_ShouldAcceptBothForms(string name, string value, string property)
    {
        foreach (string[] args in new[] { new[] { name, value }, new[] { name + "=" + value } })
        {
            var options = new KubernetesGatewayOptions();
            KubernetesGatewayCommandLine.Apply(options, args);
            typeof(KubernetesGatewayOptions).GetProperty(property)!.GetValue(options)!.ToString().ShouldBe(value);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - CLI: Missing recognized values identify the switch")]
    [InlineData("--context")]
    [InlineData("--kubeconfig")]
    [InlineData("--cohesion-system-namespace")]
    [InlineData("--cohesion-system-image")]
    [InlineData("--cohesion-system-service-account")]
    [InlineData("--cohesion-system-storage")]
    [InlineData("--control-plane-host")]
    [InlineData("--control-plane-expose")]
    public void Apply_OnMissingValue_ShouldNameSwitch(string name)
    {
        foreach (string[] args in new[] { new[] { name }, new[] { name + "=" }, new[] { name, " " }, new[] { name, "--unknown" } })
        {
            Should.Throw<ArgumentException>(() => KubernetesGatewayCommandLine.Apply(new(), args)).Message.ShouldContain(name, Case.Sensitive);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - CLI: Bootstrap boolean accepts bare and explicit forms")]
    [InlineData("--bootstrap-apply", true)]
    [InlineData("--bootstrap-apply=true", true)]
    [InlineData("--bootstrap-apply=false", false)]
    [InlineData("--bootstrap-apply false", false)]
    [InlineData("--bootstrap-apply true", true)]
    public void Apply_OnBootstrapBoolean_ShouldAcceptForms(string arguments, bool expected)
    {
        var options = new KubernetesGatewayOptions { BootstrapApply = false };
        KubernetesGatewayCommandLine.Apply(options, arguments.Split(' '));
        options.BootstrapApply.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - CLI: Hook is public and sets export path before common factory configuration")]
    public void Apply_OnPublicHook_ShouldConfigureBeforeCommonFactory()
    {
        MethodInfo method = typeof(KubernetesGatewayCommandLine).GetMethod("Apply", [typeof(KubernetesGatewayOptions), typeof(string[])])!;
        method.IsPublic.ShouldBeTrue();
        method.IsStatic.ShouldBeTrue();
        var options = new KubernetesGatewayOptions();
        KubernetesGatewayCommandLine.Apply(options, ["--unknown", "ignored"]);
        options.ExportDirectory.ShouldBe(Environment.GetEnvironmentVariable(KubernetesSystemInstallation.StateDirectoryVariable));
        GatewayControlPlane.Configure(options, GatewayRunMode.Run);
        options.ControlPlane.ShouldNotBeNull();
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "buildTransitive", "Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.props"));
        File.ReadAllText(path).ShouldContain("<CommandLineApplyMethod>global::Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.KubernetesGatewayCommandLine.Apply</CommandLineApplyMethod>", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Kubernetes] - CLI: Reject malformed exposure and bootstrap values")]
    [InlineData("--bootstrap-apply=")]
    [InlineData("--bootstrap-apply=invalid")]
    [InlineData("--control-plane-expose=invalid")]
    public void Apply_OnInvalidValue_ShouldReject(string argument) =>
        Should.Throw<ArgumentException>(() => KubernetesGatewayCommandLine.Apply(new(), [argument]));
}
