using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using k8s;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

public class KubernetesNamespaceManagerTests
{
    private const string NamespaceName = "appa";
    private const string Owner = "appa@gateway";

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Namespace delete: Should wait until the namespace is gone")]
    public async Task DeleteAsync_OnTerminatingNamespace_ShouldWaitUntilNotFound()
    {
        // Arrange
        var handler = new NamespaceHandler(notFoundOnRead: 3);
        using var client = CreateClient(handler);
        var manager = new KubernetesNamespaceManager(client, Owner);

        // Act
        await manager.DeleteAsync(NamespaceName, Owner);

        // Assert
        handler.DeleteCount.ShouldBe(1);
        handler.ReadCount.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Kubernetes] - Namespace delete: Should honor caller cancellation while waiting")]
    public async Task DeleteAsync_OnNamespaceStillTerminating_ShouldHonorCancellation()
    {
        // Arrange
        var handler = new NamespaceHandler(notFoundOnRead: null);
        using var client = CreateClient(handler);
        var manager = new KubernetesNamespaceManager(client, Owner);
        using var cancellation = new CancellationTokenSource();

        // Act
        Task deletion = manager.DeleteAsync(NamespaceName, Owner, cancellation.Token);
        await handler.PollReadObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(() => deletion);
        handler.DeleteCount.ShouldBe(1);
        handler.ReadCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    private static k8s.Kubernetes CreateClient(DelegatingHandler handler) =>
        new(
            new KubernetesClientConfiguration
            {
                Host = "https://kubernetes.test",
            },
            [handler]);

    private sealed class NamespaceHandler : DelegatingHandler
    {
        private const string NamespaceDocument = """
            {
              "apiVersion": "v1",
              "kind": "Namespace",
              "metadata": {
                "name": "appa",
                "uid": "namespace-uid",
                "resourceVersion": "42",
                "annotations": {
                  "cohesion.io/owner": "appa@gateway"
                }
              }
            }
            """;

        private const string SuccessDocument = """
            {
              "apiVersion": "v1",
              "kind": "Status",
              "status": "Success",
              "code": 200
            }
            """;

        private const string NotFoundDocument = """
            {
              "apiVersion": "v1",
              "kind": "Status",
              "status": "Failure",
              "message": "namespaces \"appa\" not found",
              "reason": "NotFound",
              "code": 404
            }
            """;

        private readonly int? _notFoundOnRead;
        private int _deleteCount;
        private int _readCount;

        public NamespaceHandler(int? notFoundOnRead)
        {
            _notFoundOnRead = notFoundOnRead;
        }

        public int DeleteCount => Volatile.Read(ref _deleteCount);

        public int ReadCount => Volatile.Read(ref _readCount);

        public TaskCompletionSource PollReadObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.RequestUri.ShouldNotBeNull();
            request.RequestUri.AbsolutePath.ShouldBe($"/api/v1/namespaces/{NamespaceName}");

            if (request.Method == HttpMethod.Delete)
            {
                _ = Interlocked.Increment(ref _deleteCount);
                return Task.FromResult(JsonResponse(request, HttpStatusCode.OK, SuccessDocument));
            }

            if (request.Method == HttpMethod.Get)
            {
                int readCount = Interlocked.Increment(ref _readCount);
                if (readCount >= 2)
                {
                    PollReadObserved.TrySetResult();
                }

                return Task.FromResult(
                    _notFoundOnRead == readCount
                        ? JsonResponse(request, HttpStatusCode.NotFound, NotFoundDocument)
                        : JsonResponse(request, HttpStatusCode.OK, NamespaceDocument));
            }

            throw new InvalidOperationException($"Unexpected Kubernetes request method '{request.Method}'.");
        }

        private static HttpResponseMessage JsonResponse(
            HttpRequestMessage request,
            HttpStatusCode statusCode,
            string document) =>
            new(statusCode)
            {
                Content = new StringContent(document, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
    }
}
