using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

public class DockerProbeRunnerTests
{
    [Fact(DisplayName = "Cohesion Test [Docker] - HTTPS probe: Should accept a service certificate through a loopback-only binding")]
    public async Task RunAsync_OnLoopbackHttpsWithUntrustedServiceCertificate_ShouldReachHealthyEndpoint()
    {
        // Arrange
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=resource.service",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using X509Certificate2 generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(5));
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.Exportable
                | X509KeyStorageFlags.PersistKeySet
                | X509KeyStorageFlags.UserKeySet);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int hostPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task server = ServeHealthyHttpsAsync(listener, certificate, timeout.Token);
        await using var engineServer = new FakeDockerEngine();
        using var engine = new DockerEngineClient(engineServer.Endpoint);
        using var runner = new DockerProbeRunner(
            engine,
            new DockerGatewayOptions { ProbeTimeout = TimeSpan.FromSeconds(2) });
        var probe = new DockerProbePlan(
            "readiness",
            "https",
            ProbeKind.Http,
            "https",
            8443,
            "tcp",
            "/readyz",
            []);
        var inspection = new DockerContainerInspectResponse
        {
            NetworkSettings = new DockerNetworkSettings
            {
                Ports = new Dictionary<string, DockerPortBinding[]?>
                {
                    ["8443/tcp"] =
                    [
                        new DockerPortBinding
                        {
                            HostIp = "127.0.0.1",
                            HostPort = hostPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        },
                    ],
                },
            },
        };

        // Act
        DockerProbeResult result = await runner.RunAsync(
            "container-1",
            probe,
            inspection,
            timeout.Token);
        await server;

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    private static async Task ServeHealthyHttpsAsync(
        TcpListener listener,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        await stream.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
            cancellationToken).ConfigureAwait(false);

        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
        {
        }

        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
