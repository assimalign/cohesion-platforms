using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using k8s;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// The gateway that realizes an <see cref="IApplicationModel"/> onto a Kubernetes cluster.
/// Its current scaffold establishes the cluster connection and an application-scoped namespace.
/// </summary>
/// <remarks>
/// This pre-plan skeleton still couples namespace cleanup to observer shutdown. The plan compiler
/// and controller in work item L04.01.03.03 replace that path with non-destructive stop and
/// destructive uninstall; container-image gathering lands with work item L04.01.03.05.
/// </remarks>
public sealed class KubernetesGateway : ApplicationGateway
{
    private readonly KubernetesGatewayOptions _options;
    private readonly InMemoryResourceStateManager _state = new();

    private IKubernetes? _client;
    private string? _namespace;

    /// <summary>
    /// Initializes a new <see cref="KubernetesGateway"/> with default options.
    /// </summary>
    public KubernetesGateway()
        : this(new KubernetesGatewayOptions())
    {
    }

    /// <summary>
    /// Initializes a new <see cref="KubernetesGateway"/> with the given options.
    /// </summary>
    /// <param name="options">The options controlling cluster connection, apply, and teardown.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public KubernetesGateway(KubernetesGatewayOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc/>
    public override ResourceName Name => "kubernetes";

    /// <summary>
    /// The built-in platform controllers. Registered overrides from the common options are
    /// consulted first; the built-in plan controller lands with work item L04.01.03.03.
    /// </summary>
    protected override IReadOnlyList<IApplicationResourceController> Controllers => Array.Empty<IApplicationResourceController>();

    /// <inheritdoc/>
    protected override IApplicationResourceStateManager State => _state;

    /// <inheritdoc/>
    protected override TimeSpan ReadinessBudget => _options.ReadinessBudget;

    /// <inheritdoc/>
    protected override Task<IResourceArtifact> GatherAsync(IApplicationResource resource, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException(
            $"The Kubernetes gateway cannot gather an artifact for resource '{resource.Name}' yet: " +
            "container-image gathering (digest-pinned image resolution via the image index) lands with " +
            "work item L04.01.03.05. Use the local gateway until then.");
    }

    /// <inheritdoc/>
    protected override async Task StartObserverAsync(IApplicationModel model, CancellationToken cancellationToken)
    {
        string namespaceName = ResolveNamespaceName(model);

        _client ??= KubernetesClientFactory.Create(_options);

        var manager = new KubernetesNamespaceManager(_client, _options.FieldManager);
        await manager.EnsureAsync(namespaceName, cancellationToken).ConfigureAwait(false);

        _namespace = namespaceName;
    }

    /// <inheritdoc/>
    protected override async Task StopObserverAsync(CancellationToken cancellationToken)
    {
        IKubernetes? client = _client;
        string? namespaceName = _namespace;
        _client = null;
        _namespace = null;

        if (client is null)
        {
            return;
        }

        try
        {
            if (namespaceName is not null)
            {
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                grace.CancelAfter(_options.StopGrace);

                var manager = new KubernetesNamespaceManager(client, _options.FieldManager);
                try
                {
                    await manager.DeleteAsync(namespaceName, grace.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Legacy scaffold behavior: namespace cleanup is suppressed here so observer
                    // shutdown remains best-effort. The plan-controller item moves deletion to
                    // UninstallAsync, where failures are aggregated and reported after every
                    // reverse-order deletion has been attempted.
                }
            }
        }
        finally
        {
            client.Dispose();
        }
    }

    // The application namespace is the application name lowercased, and must be a valid
    // RFC 1123 DNS label: 1-63 characters of [a-z0-9-], starting and ending alphanumeric.
    private static string ResolveNamespaceName(IApplicationModel model)
    {
        string name = model.Name.Value.ToLowerInvariant();

        if (!IsValidRfc1123Label(name))
        {
            throw new InvalidOperationException(
                $"The application name '{model.Name.Value}' cannot be used as a Kubernetes namespace: " +
                $"lowercased it is '{name}', which is not a valid RFC 1123 DNS label. Use 1-63 " +
                "lowercase alphanumeric characters or '-', starting and ending with an alphanumeric character.");
        }

        return name;
    }

    private static bool IsValidRfc1123Label(string value)
    {
        if (value.Length is 0 or > 63)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            bool isAlphanumeric = character is (>= 'a' and <= 'z') or (>= '0' and <= '9');

            if (isAlphanumeric)
            {
                continue;
            }

            if (character is not '-' || i == 0 || i == value.Length - 1)
            {
                return false;
            }
        }

        return true;
    }
}
