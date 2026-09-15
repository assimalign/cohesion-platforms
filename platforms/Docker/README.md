# Docker (platform area)

Docker realizes Cohesion application models through one pure `DockerPlanCompiler`, one
level-triggered controller, and one events-plus-inspect observer. Podman's Docker-compatible API
is the supported local engine. The application model and its validated `cohesion/plan/v1` plans
remain the desired-state source.

## Projects

`Assimalign.Cohesion.ApplicationModel.Gateway.Docker` contains the gateway, compiler, controller,
BCL Engine API client, observer, and daemon-free Compose-style renderer. Shared image-index and
OCI infrastructure belongs to `platforms/Containers`.

## Platform behavior

- Each resource becomes one container on an application network with stable service aliases.
  `DaemonSet` means one container on the selected engine; `Job` runs once. Unsupported replicas
  fail validation before gathering instead of being collapsed.
- Claims become named volumes. Configuration files are staged before start; Secret mounts,
  bootstrap credentials, trust bundles, and explicit telemetry credentials use tmpfs paths and
  sensitive archive entries. Sensitive data is never moved to a disk volume as a fallback.
- Endpoint schemes come from `PortBinding.Scheme`; legacy omitted schemes retain the exposure,
  HTTP-probe, then transport fallback. A nonempty endpoint certificate must name a Secret mount,
  except case-insensitive `public`. Errors name both the endpoint and the invalid mount.
- Explicit probes are preserved. When no readiness mapping exists and `plan.ControlPlane` names
  an endpoint, readiness uses HTTP at that endpoint's port and control-plane path. Private probes
  receive loopback-only ephemeral host bindings. An explicit `None` readiness mapping remains off.
- The observer alone publishes local lifecycle and endpoints. Liveness failure publishes
  non-gating `Degraded`; repeated failure can request a supervised restart. Restart policy comes
  from `plan.Workload.RestartPolicy`, with manifest fallback only for legacy omission. `Always`,
  `OnFailure`, and `Never` are gateway-supervised; Engine restart stays `no` to keep one restart
  owner and restage sensitive inputs. Configuration/startup exits remain final. Jobs run once.
- Stop gracefully stops containers and releases supervision while retaining persistent network
  and claim state. Teardown deletes in reverse order and continues after failures. Shared networks
  with custom controllers remain conservatively retained until their delete outcomes are exposed.
- Images are digest-pinned. An optional application image index is validated against the manifest;
  a pinned registry wins over `ContainerRegistry`. Gather locates/verifies/loads/pulls existing
  artifacts and never builds. Containers run by verified immutable engine image ID.

## Command line, rendering, and control plane

`DockerGatewayCommandLine.Apply(options, args)` handles `--docker-host`, repeatable
`--image-archive=<digest-reference>=<path>`, and `--control-plane-bind`. Every switch accepts either
`--name=value` or `--name value`; missing values fail and unknown arguments are ignored. Bind
values accept localhost or an IP, optionally with a port, or an absolute HTTP URI. A bare host uses
port zero. The optional `ControlPlaneAddress` is an HTTP bind address; omitted means loopback and
an ephemeral port.

The provider metadata names the public static method as
`global::Assimalign.Cohesion.ApplicationModel.Gateway.Docker.DockerGatewayCommandLine.Apply`.
Generated SDK setup and `UseDockerGateway(args, configure)` apply CLI values before the configure
callback. Common gateway options are applied by `ApplicationGatewayCommandLine`.

`IApplicationGatewayRenderer.RenderAsync` enables `--mode render --gateway docker`, preserving
model and plan order and resolving only manifest/index artifact identities. It never gathers,
opens an engine connection, or resolves credentials. Mount paths remain visible with empty preview
content. Direct `IDockerComposeRenderer.Render` still requires resolved mount inputs. Output is a
review document, not a Compose deployment controller.

The domain gateway's ControlPlane package supplies the listener and authenticated discovery.
`GatewayControlPlane.Configure` uses `ExportDirectory` (default `.cohesion`) to publish
`<application>/control-plane.json` beside `export.json`. Docker only selects the host bind address;
it does not implement token policy. The listener serves authenticated
`GET /cohesion/v1/application` and rejects invalid credentials.

## Delivery boundaries

`ResourceInputs.TrustBundle`, when nonempty, becomes sensitive `/var/run/cohesion/trust.pem` with
`ResourceEnvironment.TrustBundlePath`. The internal compiler-only `telemetryHeaders` input becomes
sensitive `/var/run/cohesion/telemetry.headers` with `ResourceEnvironment.TelemetryHeadersPath`.
Empty inputs add no file, tmpfs, or environment entry. The upstream telemetry carrier remains
private, so normal Docker reconciliation cannot obtain those headers yet; Docker does not invent
telemetry endpoint/protocol values. The public application trust key remains ordinary public data.

Sensitive archive upload occurs after container start, leaving a PID 1 bootstrap race. Live tmpfs
visibility also depends on the engine's archive implementation; Moby's private archive filesystem
view is an existing limitation. Hermetic compilation/staging coverage does not establish portable,
atomic live sensitive-input population. The secure tmpfs design is retained while that seam is open.

Port-forward management and cross-gateway topology integration remain open. Full real Docker/Podman
end-to-end coverage remains item `.05` / #28; smoke tests skip without a compatible daemon. Engine
pull has no private-registry auth carrier, and OCI-layout load still needs the real-daemon matrix.

## Layering and AOT

The shipped project depends only on generic ApplicationModel/Gateway contracts and shared Containers;
COHPLT001 forbids resource-area ApplicationModel, Hosting, Application runtimes, and Microsoft.Extensions
assemblies. The ControlPlane package reference is test-only here. The library's explicit
`IsAotCompatible=true` and `RequiresJit=false` are the scoped item 36 choice, not a repository-wide
mandate; the upstream SDK's early auto-AOT allowlist remains a separate integration concern.

See the [project overview](Assimalign.Cohesion.ApplicationModel.Gateway.Docker/docs/OVERVIEW.md),
[design](Assimalign.Cohesion.ApplicationModel.Gateway.Docker/docs/DESIGN.md), and
[assembly reference](Assimalign.Cohesion.ApplicationModel.Gateway.Docker/docs/Assembly/Assimalign.Cohesion.ApplicationModel.Gateway.Docker/OVERVIEW.md).
