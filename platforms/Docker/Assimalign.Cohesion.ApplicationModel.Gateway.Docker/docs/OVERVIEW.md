# Assimalign.Cohesion.ApplicationModel.Gateway.Docker — Overview

`DockerGateway : ApplicationGateway` realizes validated plans through a Docker-compatible engine.
One compiler handles every supported resource kind, one controller reconciles desired state, and
one observer publishes lifecycle and endpoint observations. The base owns ordering, readiness gates,
rollback, and teardown. The package depends on generic Cohesion contracts and shared Containers.

## Usage

```csharp
builder.UseDockerGateway(args, options =>
{
    options.ExportDirectory = ".cohesion";
    options.PublicHost = "localhost";
});
```

The generated SDK provider and this convenience extension invoke
`DockerGatewayCommandLine.Apply` before the configure callback. It accepts `--docker-host`,
repeatable `--image-archive=<digest-reference>=<path>`, and `--control-plane-bind` in separated or
`=` form. Unknown switches are ignored; recognized switches require values. A control-plane bind
is localhost or an IP, optionally with a port, or an absolute HTTP URI; bare hosts request port
zero. `DockerGatewayOptions.ControlPlaneAddress` validates the same listener constraints.

The domain gateway installs the shared listener through `GatewayControlPlane.Configure` from
`Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane`. Metadata is published at
`<ExportDirectory>/<application>/control-plane.json` beside `export.json`; the default root is
`.cohesion`. Docker supplies the host binding and the shared package owns authentication and
`GET /cohesion/v1/application`.

## Compilation and lifecycle

- Exactly one container per plan, one application network, stable aliases, and named claim volumes.
  `DaemonSet` is one engine-local container and `Job` runs once; unsupported replicas fail early.
- Endpoint bindings prefer their declared URI scheme, retaining the historical fallback for legacy
  omitted values. A certificate must name a Secret mount unless empty or case-insensitive `public`.
- Explicit readiness/startup/liveness mappings remain intact. With no readiness mapping, a populated
  control-plane endpoint supplies HTTP readiness at its port and declared path. Probes use private
  loopback host bindings, including for otherwise private endpoints. `None` suppresses the fallback.
- Restart policy comes from the workload plan. Only legacy omission reads the generic manifest.
  `Always`, `OnFailure`, and `Never` are supervised by the gateway with Engine restart disabled;
  Jobs run once and configuration/startup exit codes remain final. Normal stop preserves networks
  and claims; teardown removes owned objects in reverse order, continuing after failure.
- Manifest/index identity is digest-pinned. Gather verifies existing images, loads a verified archive,
  or pulls by digest without building; a custom realizer cannot substitute another artifact.

## Offline render

`IApplicationGatewayRenderer.RenderAsync(models, writer, cancellationToken)` implements SDK
`--mode render --gateway docker`. It preserves model/plan order, resolves artifact identity from the
manifest or image index, and compiles with `ResourceInputs.Empty`. It never gathers, calls a realizer,
opens the engine, or resolves a secret. Preview mounts retain their paths and sensitivity without
contents. The direct `IDockerComposeRenderer.Render` API still rejects unresolved live inputs.

The deterministic Compose-style output is review/debug data. `restart: no` reflects Engine policy;
`x-cohesion.restart_policy` and `restart_owner: gateway` describe supervision. Compose does not become
a second lifecycle controller.

## Sensitive inputs and remaining work

Configuration is staged while stopped. Secret mounts and bootstrap credentials use sensitive archives
and tmpfs. A nonempty `ResourceInputs.TrustBundle` adds `/var/run/cohesion/trust.pem` and
`ResourceEnvironment.TrustBundlePath`. Explicit internal `telemetryHeaders` adds
`/var/run/cohesion/telemetry.headers` and `ResourceEnvironment.TelemetryHeadersPath` on the same tmpfs.
Empty trust/header inputs add nothing. Normal reconciliation cannot access upstream's private telemetry
carrier yet; no telemetry endpoint or protocol is synthesized. Public trust keys remain public data.

Sensitive files are uploaded after start, so PID 1 can race materialization. Live tmpfs visibility
also depends on the daemon's archive implementation; the existing Moby private-filesystem limitation
is not resolved by compilation tests. Secure storage is retained without falling back to disk.
Port-forward and topology integration remain open, along with full Docker/Podman E2E, private-registry
auth, OCI archive compatibility, and shared-network teardown outcomes for custom controllers.

The explicit trim/AOT posture is scoped to this library. The upstream SDK early auto-AOT decision
is separate from provider `RequiresJit=false` metadata. See [DESIGN.md](DESIGN.md) for ownership and
compatibility reasoning.
