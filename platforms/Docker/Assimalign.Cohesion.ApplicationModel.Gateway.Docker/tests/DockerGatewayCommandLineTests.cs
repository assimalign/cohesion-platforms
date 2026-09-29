using System;
using System.IO;
using System.Reflection;
using System.Xml.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public class DockerGatewayCommandLineTests
{
    [Theory(DisplayName = "Cohesion Test [Docker] - CLI: Should apply each Docker switch in either value form")]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_OnSupportedSwitchForms_ShouldPopulateOptions(bool equalsForm)
    {
        // Arrange
        string image = $"registry.example/worker@sha256:{new string('a', 64)}";
        string archive = image + "=images/file=part.tar";
        string[] args = equalsForm
            ? ["--docker-host=http://localhost:2375", "--image-archive=" + archive, "--control-plane-bind=127.0.0.1:8085"]
            : ["--docker-host", "http://localhost:2375", "--image-archive", archive, "--control-plane-bind", "127.0.0.1:8085"];
        var options = new DockerGatewayOptions();

        // Act
        DockerGatewayCommandLine.Apply(options, args);

        // Assert
        options.EngineEndpoint.ShouldBe(new Uri("http://localhost:2375"));
        options.ImageArchives[image].ShouldBe("images/file=part.tar");
        options.ControlPlaneAddress.ShouldBe(new Uri("http://127.0.0.1:8085"));
    }

    [Theory(DisplayName = "Cohesion Test [Docker] - CLI: Should reject missing values for every Docker switch")]
    [InlineData("--docker-host")]
    [InlineData("--image-archive")]
    [InlineData("--control-plane-bind")]
    public void Apply_OnMissingValue_ShouldRejectBothForms(string argument)
    {
        // Arrange
        var options = new DockerGatewayOptions();

        // Act / Assert
        Should.Throw<ArgumentException>(() => DockerGatewayCommandLine.Apply(options, [argument]));
        Should.Throw<ArgumentException>(() => DockerGatewayCommandLine.Apply(options, [argument + "="]));
        Should.Throw<ArgumentException>(() => DockerGatewayCommandLine.Apply(options, [argument, "--unknown"]));
        Should.Throw<ArgumentException>(() => DockerGatewayCommandLine.Apply(options, [argument, " "]));
    }

    [Theory(DisplayName = "Cohesion Test [Docker] - CLI: Should accept HTTP localhost and IP bind forms")]
    [InlineData("localhost", "http://localhost:0/")]
    [InlineData("127.0.0.1", "http://127.0.0.1:0/")]
    [InlineData("0.0.0.0:8123", "http://0.0.0.0:8123/")]
    [InlineData("[::1]:8123", "http://[::1]:8123/")]
    [InlineData("::1", "http://[::1]:0/")]
    [InlineData("http://127.0.0.1:8123", "http://127.0.0.1:8123/")]
    [InlineData("http://localhost:8123/path", "http://localhost:8123/path")]
    [InlineData("http://user@localhost:8123", "http://user@localhost:8123/")]
    [InlineData("http://localhost:8123/?query=yes", "http://localhost:8123/?query=yes")]
    [InlineData("http://localhost:8123/#fragment", "http://localhost:8123/#fragment")]
    [InlineData("http://[::1]:8123/path?query=yes#fragment", "http://[::1]:8123/path?query=yes#fragment")]
    public void Apply_OnControlPlaneBind_ShouldNormalizeAddress(string input, string expected)
    {
        // Arrange
        var options = new DockerGatewayOptions();

        // Act
        DockerGatewayCommandLine.Apply(options, ["--control-plane-bind", input]);

        // Assert
        options.ControlPlaneAddress.ShouldBe(new Uri(expected));
        new DockerGateway(options).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Docker] - Control plane: Should reject unsupported bind addresses")]
    [InlineData("https://127.0.0.1:8123")]
    [InlineData("http://gateway.example:8123")]
    [InlineData("relative", false)]
    public void Constructor_OnInvalidControlPlaneAddress_ShouldReject(string address, bool absolute = true)
    {
        // Arrange
        var options = new DockerGatewayOptions
        {
            ControlPlaneAddress = new Uri(address, absolute ? UriKind.Absolute : UriKind.Relative),
        };

        // Act / Assert
        Should.Throw<ArgumentException>(() => new DockerGateway(options)).ParamName
            .ShouldBe(nameof(DockerGatewayOptions.ControlPlaneAddress));
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - CLI: Should ignore unknown arguments and leave defaults intact")]
    public void Apply_OnUnknownArguments_ShouldLeaveOptionsIntact()
    {
        // Arrange
        var options = new DockerGatewayOptions();

        // Act
        DockerGatewayCommandLine.Apply(options, ["--unknown", "value", "--unknown=other"]);

        // Assert
        options.EngineEndpoint.ShouldBeNull();
        options.ControlPlaneAddress.ShouldBeNull();
        options.ImageArchives.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Docker] - Provider: Should advertise the actual public static CLI method")]
    public void Metadata_OnProvider_ShouldResolvePublicStaticApply()
    {
        // Arrange
        XDocument metadata = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "provider.props"));
        string advertised = metadata.Root!.Element("ItemGroup")!.Element("CohesionGatewayProvider")!
            .Element("CommandLineApplyMethod")!.Value;
        MethodInfo? method = typeof(DockerGatewayCommandLine).GetMethod(
            nameof(DockerGatewayCommandLine.Apply), BindingFlags.Public | BindingFlags.Static,
            [typeof(DockerGatewayOptions), typeof(string[])]);

        // Act / Assert
        advertised.ShouldBe("global::" + typeof(DockerGatewayCommandLine).FullName + ".Apply");
        method.ShouldNotBeNull().ReturnType.ShouldBe(typeof(void));
        method.CreateDelegate<Action<DockerGatewayOptions, string[]>>()(
            new DockerGatewayOptions(), ["--control-plane-bind=localhost"]);
    }
}
