# Assimalign.Cohesion.ApplicationModel.Gateway.Docker — Design

The authority is the signed `docs/DEVELOPER_EXPERIENCE_DESIGN.md` and `docs/REALIZATION_PLAN.md`
in Cohesion. This package implements image item 35, Docker item 36, and the bounded item 37
integration with generated command-line setup, the shared gateway control plane, and the completed
v1 plan fields.

## Ownership and compiler boundary

`DockerGateway` derives from `ApplicationGateway`. The base owns dependency order, initial
plan-derived gates, blocked propagation, startup rollback, normal stop, and destructive uninstall.
The Docker compiler is pure: plans, artifact identity, resolved inputs, immutable dependency
observations, and explicit platform values produce engine object descriptions. Resource kind,
area assembly, and CLR type never select a different compiler.

One level-triggered `DockerPlanController` converges network, claim volumes, and containers.
Registered domain controllers are consulted first in registration order. Labels identify owner,
application, resource, plan hash, and runtime hash; names alone never prove ownership. Canonical
plan JSON determines only the plan hash. Credential, artifact, dependency, and platform-option
changes affect runtime reconciliation independently.

The compiler rejects unsupported schema/specification fields and replicas before engine contact.
Unknown advisory hint keys warn once per gateway session or render operation. Each accepted plan
creates one container. `DaemonSet` is explicitly one container on this engine; `Job` is run once
and its successful stop satisfies the plan gate. Multiple replicas are rejected rather than
silently collapsed.

## Endpoints, probes, and restart

One application network carries stable resource/service aliases. Observations use canonical
`System.Uri` endpoint values and `ResourceEnvironment` keys. `PortBinding.Scheme` is authoritative;
legacy omission retains the exposure, explicit HTTP probe, then transport fallback. Public
exposures receive host ports and private endpoints remain on the application network.

Explicit readiness, startup, and liveness probes map unchanged. When readiness is absent and
`plan.ControlPlane.Endpoint` is populated, the compiler creates an HTTP readiness probe using that
endpoint's port/scheme and `ControlPlane.Path`. An explicit `None` mapping disables readiness and
prevents fallback. No default liveness or startup probe is invented. Every HTTP/TCP-probed endpoint
receives a loopback-only ephemeral host binding because Desktop/Podman hosts cannot portably reach
container IPs. That binding is never promoted into public discovery. HTTPS probes use the existing
loopback reachability/status behavior, matching Kubernetes probe certificate treatment.

One events-plus-inspect observer is the sole writer of Docker state. Events reduce latency and full
inspection recovers level truth after disconnects. Readiness gates only initial admission; a later
liveness failure publishes `Degraded` without re-gating dependents. Repeated failure requests a
bounded supervised restart.

The controller reads `plan.Workload.RestartPolicy`; only the empty legacy sentinel falls back to
`IManifestResource.Manifest.Lifecycle.RestartPolicy`. `Always` restarts after eligible exits,
`OnFailure` after failures, and `Never` does not restart. Jobs remain run once. The
compiler rejects an unrecognized nonempty restart policy with `InvalidDataException` before
engine contact. The existing `cohesion/sysexits/v1` validation keeps configuration/startup exits final. Engine restart policy
stays `no`: allowing both Engine and gateway supervision would create competing owners and skip
restaging sensitive runtime inputs. Render describes the desired policy in
`x-cohesion.restart_policy` and identifies `restart_owner: gateway` while retaining `restart: no`.

Normal stop uses `plan.Workload.StopGraceSeconds`, then releases observer/supervisor lifetimes while
retaining networks and claims. Uninstall attempts reached resources in reverse order despite earlier
failures. A shared network is retained for custom or mixed-controller models because the base does
not yet expose every custom controller's delete outcome.

## Mounts, certificates, and sensitive runtime carriers

Volume claims become deterministic named volumes. Ordinary Configuration inputs are archived into
the stopped container before PID 1 starts. A nonempty `PortBinding.Certificate` must name a Secret
mount, except case-insensitive reserved `public`; empty certificates are valid independently on
other endpoints. Failure is `InvalidDataException` naming endpoint and mount. This validates the
portable mount relationship, not certificate PEM contents or certificate issuance.

Secret mounts and nonempty bootstrap credentials are sensitive archive entries below container
tmpfs mounts. `ResourceInputs.TrustBundle` adds sensitive `/var/run/cohesion/trust.pem` and sets
`ResourceEnvironment.TrustBundlePath`. The explicit internal compiler input
`ReadOnlyMemory<byte> telemetryHeaders = default` adds sensitive
`/var/run/cohesion/telemetry.headers` and `ResourceEnvironment.TelemetryHeadersPath`. Their shared
parent `/var/run/cohesion` is mounted once with restrictive tmpfs options. Empty inputs add no file,
mount, or environment entry. Public application trust keys remain public environment data.

The compiler never synthesizes telemetry endpoint/protocol values or reads the process environment
to obtain credentials. Upstream's telemetry carrier is private and is not exposed on
`ResourceInputs`; normal reconcile cannot supply headers until a public upstream seam exists.
Direct internal tests cover the available compiler input without implying live telemetry bootstrap.

Sensitive upload follows container start, leaving the accepted PID 1 staging race. Engine archive
visibility remains a separate existing limitation: Moby opens a private filesystem view whose tmpfs
writes may not populate the process-visible mount. The implementation preserves tmpfs and never
weakens sensitive material to disk as a workaround. Portable atomic delivery needs a cooperative
bootstrap or upstream engine seam; hermetic archive tests cannot establish that behavior.

## Artifact acquisition and Engine client

The package uses a small typed BCL HTTP client over HTTP/HTTPS, Unix sockets, and Windows named
pipes with source-generated JSON. This preserves one transport surface without Docker.DotNet or a
private named-pipe shim around the currently unavailable Cohesion Connections package. Unversioned
`GET /version` supplies the daemon range, intersected with client v1.25–v1.51; the highest common
version is cached. Malformed ranges or no overlap fail before versioned requests.

Gather never builds. A configured `cohesion/images/v1` application index must match the model and
manifest's authority-free repository/digest. A pinned entry registry wins; `ContainerRegistry`
binds only an omitted registry. Archive paths resolve relative to the index without traversal.
Live acquisition needs a registry or archive for late-bound entries. The realizer verifies an
existing image, loads a verified manifest/config/layer closure, or pulls by digest, and the gateway
returns its verified immutable engine image ID. Custom realizers cannot substitute another artifact.
`ImageArchives` remains the no-index bridge. Private registry auth and cross-engine OCI-layout load
compatibility are still separate delivery work.

## Generated command line and offline render

`buildTransitive` declares gateway type, options type, `RequiresJit=false`, and the exact hook
`global::Assimalign.Cohesion.ApplicationModel.Gateway.Docker.DockerGatewayCommandLine.Apply`.
This public static command-line class is the item 37 scoped interface-first deviation required by
the upstream generator's static invocation contract. The convenience extension invokes the same
method after common CLI processing, before the user's configure callback.
The SDK invokes this hook before `GatewayControlPlane.Configure` selects the metadata directory,
so an `ExportDirectory` established during provider setup is honored by the shared server.

The parser accepts `--docker-host`, repeated `--image-archive`, and `--control-plane-bind` in
separated or equals form. Recognized missing values throw; unknown switches are ignored. Archive
mapping splits at the first equals so file paths can themselves contain equals. Bind accepts a
localhost/IP host, host and port, or absolute HTTP URI, including IPv6. Bare hosts request an
ephemeral port. `ControlPlaneAddress` matches the upstream listener's checks: an absolute HTTP URI
whose host is localhost or an IP address. Other URI components do not change the listener's host
and port. Validation uses `Uri.Host`, including bracketed IPv6 accepted by `IPAddress.TryParse`;
upstream uses `IdnHost` when publishing the bound address. Omitted address delegates to IPv4 loopback/port-zero
behavior.

`IApplicationGatewayRenderer.RenderAsync` supplies SDK `--mode render` dispatch. Models and their
plans retain supplied order; artifact identity is read from manifest/index without image gathering,
archive inspection, engine construction, or a custom realizer call. Compilation uses
`ResourceInputs.Empty` with an explicit internal preview flag: non-volume mounts retain paths and
sensitivity but have no resolved contents. No fake credential is created. The direct public
`IDockerComposeRenderer.Render` retains strict resolved-input requirements. The writer receives
output only after all plans compile, and cancellation is honored before and during work.

Compose-style output is inspection data, not another desired-state source or lifecycle controller.
The five canonical plan fixtures are copied verbatim from upstream; Docker's one-replica adjustment
exists only in the test harness and preserves restart policy and `ControlPlane`. The web harness
supplies deterministic test-only TLS Secret bytes; rendered input metadata never emits those bytes.
Set `COHESION_RENDER_OUTPUT` to a scratch directory when explicitly regenerating render fixtures.
The tests export actual output under `Docker/` while retaining the golden assertion; review and
copy the output into `tests/Fixtures`, then run the render tests twice.

## Shared control plane

The domain gateway selects `Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane`, whose SDK
configuration installs the factory and authenticated resolver client. Docker overrides only bind
address resolution; listener, token verification, issuer trust, and discovery stay shared.
`GatewayControlPlane.Configure` uses `ExportDirectory` (default `.cohesion`) so
`<application>/control-plane.json` sits beside `export.json`. The real-listener test uses a fake
Engine transport, authenticated `GET /cohesion/v1/application`, a real issued developer token, and
valid/wrong-audience/expired signed peer credentials. The new ControlPlane dependency is test-only
in this platform project.

## Layering, AOT, and remaining integration

Shipped references remain generic ApplicationModel/Gateway and shared Containers. COHPLT001 guards
the resolved closure against resource-area ApplicationModel, Hosting, Application runtimes, and
Microsoft.Extensions dependencies, including transitive package assemblies. The owner-corrected
rule 3 permits Hosting, Hosting.Health, and Hosting.Resources only as dependencies of the allowed
Gateway base; this platform never references those assemblies directly.

`IsAotCompatible=true` is the scoped item 36 decision; no repository-wide mandate is introduced.
The SDK's early automatic AOT selection remains independent of `RequiresJit=false` metadata.
Port-forward and cross-gateway topology integration remain open. Full real Docker/Podman E2E is
`.05` / #28, with smoke tests skipped when no compatible daemon exists. Sensitive live namespace
population, private registry authentication, OCI archive engine compatibility, and custom-controller
shared-network teardown outcomes remain explicit boundaries.
