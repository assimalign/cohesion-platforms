using System;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public class DockerEngineTests
{
    private const string DisplayPrefix = "Cohesion Test [Gateway.Docker] - Engine: ";

    [Theory(DisplayName = DisplayPrefix + "An endpoint in a docker CLI form names its transport and address")]
    [InlineData("unix:///var/run/docker.sock", "UnixSocket", "/var/run/docker.sock", null)]
    [InlineData("unix:///run/podman/podman.sock", "UnixSocket", "/run/podman/podman.sock", null)]
    [InlineData("npipe:////./pipe/docker_engine", "NamedPipe", "docker_engine", ".")]
    [InlineData("npipe://./pipe/podman-machine-default", "NamedPipe", "podman-machine-default", ".")]
    [InlineData("tcp://127.0.0.1:2375", "Http", "http://127.0.0.1:2375/", null)]
    [InlineData("http://engine:2375", "Http", "http://engine:2375/", null)]
    public void Parse_WithCliForm_ShouldNameTheTransport(string value, string transport, string address, string? server)
    {
        // Act
        DockerEngineEndpoint endpoint = DockerEngineEndpoint.Parse(value);

        // Assert
        endpoint.Transport.ToString().ShouldBe(transport);
        endpoint.Address.ShouldBe(address);
        endpoint.Server.ShouldBe(server);
        endpoint.Value.ShouldBe(value);
    }

    [Theory(DisplayName = DisplayPrefix + "An endpoint the platform does not speak is refused")]
    [InlineData("/var/run/docker.sock")]
    [InlineData("unix://")]
    [InlineData("npipe:////./docker_engine")]
    [InlineData("ftp://engine")]
    public void Parse_WithUnknownForm_ShouldThrow(string value)
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => DockerEngineEndpoint.Parse(value));
    }

    [Theory(DisplayName = DisplayPrefix + "An endpoint over TLS or SSH is not spoken yet")]
    [InlineData("ssh://user@engine")]
    [InlineData("https://engine:2376")]
    public void Parse_WithTlsOrSsh_ShouldThrow(string value)
    {
        // Act & Assert
        Should.Throw<NotSupportedException>(() => DockerEngineEndpoint.Parse(value));
    }

    [Fact(DisplayName = DisplayPrefix + "The configured endpoint wins over DOCKER_HOST and the platform's default")]
    public void Resolve_WithConfiguredEndpoint_ShouldUseIt()
    {
        // Act
        DockerEngineEndpoint configured = DockerEngineEndpoint.Resolve("tcp://127.0.0.1:2375");

        // Assert
        configured.Transport.ShouldBe(DockerEngineTransport.Http);
    }

    [Fact(DisplayName = DisplayPrefix + "Without a configured endpoint or DOCKER_HOST, the platform's default socket is used")]
    public void Resolve_WithoutEndpoint_ShouldUseThePlatformDefault()
    {
        // Arrange
        if (Environment.GetEnvironmentVariable(DockerEngineEndpoint.HostVariable) is { Length: > 0 })
        {
            return;
        }

        // Act
        DockerEngineEndpoint endpoint = DockerEngineEndpoint.Resolve(null);

        // Assert
        endpoint.Value.ShouldBe(OperatingSystem.IsWindows() ? "npipe:////./pipe/docker_engine" : "unix:///var/run/docker.sock");
    }

    [Theory(DisplayName = DisplayPrefix + "The API version is the highest both the engine and the client speak")]
    [InlineData("1.41", "1.24", "1.41")]
    [InlineData("1.47", "1.24", "1.47")]
    [InlineData("1.53", "1.44", "1.51")]
    public void Negotiate_ShouldChooseTheHighestCommonVersion(string maximum, string minimum, string expected)
    {
        // Act
        Version version = DockerEngineClient.Negotiate(new DockerVersionResponse { ApiVersion = maximum, MinimumApiVersion = minimum });

        // Assert
        version.ToString(2).ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "An engine whose versions do not overlap the client's is refused")]
    [InlineData("1.40", "1.12")]
    [InlineData("1.60", "1.55")]
    [InlineData("latest", null)]
    public void Negotiate_WithoutOverlap_ShouldThrow(string maximum, string? minimum)
    {
        // Act & Assert
        Should.Throw<NotSupportedException>(() => DockerEngineClient.Negotiate(new DockerVersionResponse { ApiVersion = maximum, MinimumApiVersion = minimum }));
    }

    [Fact(DisplayName = DisplayPrefix + "Framed logs are read as their payloads, in order; unframed logs as they are")]
    public void DemultiplexLogs_ShouldReadThePayloads()
    {
        // Arrange
        byte[] framed = [.. Frame(1, "starting\n"), .. Frame(2, "Unhandled exception.\n")];

        // Act & Assert
        DockerEngineClient.DemultiplexLogs(framed).ShouldBe("starting\nUnhandled exception.\n");
        DockerEngineClient.DemultiplexLogs(Encoding.UTF8.GetBytes("plain text\n")).ShouldBe("plain text\n");
        DockerEngineClient.DemultiplexLogs([]).ShouldBe(string.Empty);
    }

    private static byte[] Frame(byte stream, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        byte[] frame = new byte[8 + payload.Length];
        frame[0] = stream;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }
}
