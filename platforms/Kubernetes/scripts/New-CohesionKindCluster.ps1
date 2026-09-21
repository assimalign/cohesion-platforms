# Based on https://kind.sigs.k8s.io/docs/user/local-registry/.
[CmdletBinding()]
param(
    [ValidatePattern('^[a-z0-9][a-z0-9-]*$')][string] $Name = 'cohesion',
    [switch] $Recreate
)
$ErrorActionPreference = 'Stop'
$env:KIND_EXPERIMENTAL_PROVIDER = 'podman'
if ($IsWindows) {
    $kindDirectory = Join-Path $env:LOCALAPPDATA 'Microsoft/WinGet/Packages/Kubernetes.kind_Microsoft.Winget.Source_8wekyb3d8bbwe'
    if (Test-Path (Join-Path $kindDirectory 'kind.exe')) { $env:PATH = "$kindDirectory;$env:PATH" }
}

function Assert-Exit([string] $Operation) {
    if ($LASTEXITCODE -ne 0) { throw "$Operation failed (exit $LASTEXITCODE)." }
}

$clusters = @(kind get clusters)
Assert-Exit 'List Kind clusters'
if ($Name -in $clusters -and $Recreate) {
    kind delete cluster --name $Name
    Assert-Exit 'Recreate Kind cluster'
    $clusters = @($clusters | Where-Object { $_ -ne $Name })
}
podman network exists kind
if ($LASTEXITCODE -eq 1) {
    podman network create kind
    Assert-Exit 'Create kind network'
} elseif ($LASTEXITCODE -ne 0) { throw 'Cannot inspect kind network.' }

podman container exists cohesion-registry
if ($LASTEXITCODE -eq 1) {
    podman run -d --restart=always --name cohesion-registry --network kind -p 127.0.0.1:5001:5000 docker.io/library/registry:2
    Assert-Exit 'Create local registry'
} elseif ($LASTEXITCODE -ne 0) { throw 'Cannot inspect registry container.' }
else {
    $registry = (podman inspect cohesion-registry | ConvertFrom-Json)[0]
    Assert-Exit 'Inspect local registry'
    $ports = @($registry.NetworkSettings.Ports.'5000/tcp')
    if (-not ($ports | Where-Object { $_.HostPort -eq '5001' -and $_.HostIp -eq '127.0.0.1' })) {
        throw 'Existing cohesion-registry does not map 127.0.0.1:5001 to 5000. Correct its mapping before reuse.'
    }
    if (-not $registry.State.Running) { podman start cohesion-registry; Assert-Exit 'Start local registry' }
    if (-not $registry.NetworkSettings.Networks.kind) {
        podman network connect kind cohesion-registry
        Assert-Exit 'Connect local registry'
    }
}

if ($Name -notin $clusters) {
    # Containerd 2 uses the images plugin. Include the legacy table for older node images.
    @'
kind: Cluster
apiVersion: kind.x-k8s.io/v1alpha4
containerdConfigPatches:
- |-
  [plugins."io.containerd.grpc.v1.cri".registry]
    config_path = "/etc/containerd/certs.d"
  [plugins."io.containerd.cri.v1.images".registry]
    config_path = "/etc/containerd/certs.d"
'@ | kind create cluster --name $Name --config=- --wait 120s
    Assert-Exit 'Create Kind cluster'
}
$nodes = @(kind get nodes --name $Name)
Assert-Exit 'List Kind nodes'
foreach ($node in $nodes) {
    $config = podman exec $node cat /etc/containerd/config.toml
    Assert-Exit 'Read containerd configuration'
    if (($config -join "`n") -notmatch 'config_path\s*=\s*["'']/etc/containerd/certs.d["'']') {
        throw "Node $node does not enable registry config_path. Run this script with -Name $Name -Recreate."
    }
    podman exec $node mkdir -p /etc/containerd/certs.d/localhost:5001
    Assert-Exit 'Create registry configuration directory'
    @'
server = "http://cohesion-registry:5000"
[host."http://cohesion-registry:5000"]
  capabilities = ["pull", "resolve"]
'@ | podman exec -i $node cp /dev/stdin /etc/containerd/certs.d/localhost:5001/hosts.toml
    Assert-Exit 'Configure containerd registry route'
}
@'
apiVersion: v1
kind: ConfigMap
metadata:
  name: local-registry-hosting
  namespace: kube-public
data:
  localRegistryHosting.v1: |
    host: "localhost:5001"
    help: "https://kind.sigs.k8s.io/docs/user/local-registry/"
'@ | kubectl --context "kind-$Name" apply -f -
Assert-Exit 'Advertise local registry'
kubectl --context "kind-$Name" get nodes
Assert-Exit 'Verify Kind nodes'
podman ps --filter name=cohesion-registry
Assert-Exit 'Verify registry container'
foreach ($node in $nodes) {
    podman exec $node cat /etc/containerd/certs.d/localhost:5001/hosts.toml
    Assert-Exit 'Verify registry route'
}
