# Assimalign.Cohesion.ApplicationModel.Gateway.Docker — Design

The gateway half of the Docker platform pair, rebuilt on 2026-10-31 for the application-model
restart (assimalign/cohesion#1759, contracts decision C5 "platform pairs"), after the Kubernetes pair
and on its pattern. It replaces the plan-compiler implementation, which is in this repository's
history before this change. The BCL Engine API client survived, trimmed to the calls the platform
makes.

## Role

`DockerInfrastructure` is both of the gateway's platform seams:

| Seam | What it does on Docker |
| --- | --- |
| `IApplicationGatewayInfrastructureProvider` | Plans, deploys, watches, reports the outputs of, and tears down each resource as containers on the gateway's network. Its `StartAsync(context)` and `StopAsync` run the inbound channel (GD2): it polls the network's instance containers and hands their publications and renewals to `context.Publisher`. |
| `IApplicationGatewayInstanceClient` | Calls an instance's control plane on the loopback port the engine published it on. |

`builder.UseDocker(configure)`, which builds it and gives it to the gateway to own, or
`builder.UseDocker(infrastructure)`, which lends it, sets it as both (GD12). The instance half,
`Assimalign.Cohesion.ApplicationModel.Hosting.Docker`, is the other end of the channel and the
client. The two halves never reference each other: the protocol they share, variables, endpoint
forms, channel routes and documents, is one file, `Hosting.Docker/shared/DockerProtocol.cs`,
compiled into both.

## The exchange

```mermaid
sequenceDiagram
    participant G as Gateway (Gateway.Docker)
    participant E as Engine API
    participant C as Container (Hosting.Docker)
    G->>E: create network (once), pull image if missing
    G->>E: create and start one container per replica (labels, runtime contract, init)
    E->>C: start program (PID 2 under the engine's init)
    Note over C: PublishAsync opens the control-plane port, state = manifest + key, waits
    loop every second
        G->>E: list the network's containers (published ports)
        G->>C: GET 127.0.0.1:{host port}/channel
    end
    C-->>G: state: key unanswered
    G->>G: context.Publisher.PublishAsync(manifest)
    G->>C: POST /channel/answer (publication, lease)
    C-->>C: PublishAsync returns, state restored, host starts
    loop every lease / 3
        C-->>C: RenewAsync moves the renewal count
        G->>C: GET /channel (count moved)
        G->>G: context.Publisher.RenewAsync(manifest)
    end
    Note over G,C: A renewal the gateway does not know: DELETE /channel/answer/{key}, the instance publishes again
    G->>E: inspect container (host port of 9090/tcp)
    G->>C: GET or POST /actions/{name}, PUT /state/{name}
    C-->>G: ApplicationActionResult
```

## Decisions

**GD1 — The gateway pulls, over the container's published control-plane port (2026-10-31).** There
is no address both sides reach the way Kubernetes' API server is (GK1), and a container cannot count
on reaching a gateway on the engine's host: `host.docker.internal` exists on Docker Desktop only,
Podman names it `host.containers.internal` and only for some network modes, and Linux Docker needs an
explicit host-gateway mapping. What every engine does is publish a container port on its host. So
every connection goes gateway → container: the engine publishes each container's control-plane port
(9090 by default) on an ephemeral loopback port of its host, the gateway finds that port in the
engine's listing, and reads and answers the instance's channel there.

- *Verified* on a rootful Podman 5.8.6 WSL machine on Windows ARM64, whose WSL localhost forwarding
  carries a `127.0.0.1::9090` binding to the Windows host, and by the end-to-end tests.
- *Rejected:* the instance pushing to a listener of the gateway. It needs a host address reachable
  from containers on every engine, and a host firewall rule for it.
- *Rejected:* `docker exec` into the container to read its state. It needs a process per poll, and an
  exec API Podman's compatibility layer implements only in part.
- *Rejected:* host networking. It exists on Linux engines only, and gives up the network's names.

**GD2 — The instance keeps its side of the channel, and the gateway answers there (2026-10-31).** The
instance's state (`DockerChannelState`: manifest, instance key, the keys the gateway answered or
refused, and a renewal count) is the Lease of the Kubernetes pair (GK2), held in the instance's
memory and served on its port. The channel lists the network's containers every second and reads each
running one's state, all at once:

- a state whose answered key is not its instance key is a publication: the channel calls
  `context.Publisher.PublishAsync` and posts the publication, or the refusal's reason, to
  `POST /channel/answer`, which names the key it answers;
- a state whose answer is current and whose renewal count moved is a renewal: the channel calls
  `RenewAsync`, and for an instance the gateway does not know it clears the answer
  (`DELETE /channel/answer/{key}`), so the instance's next renewal reports itself unknown and it
  publishes again;
- a container that does not answer, such as one whose program has not opened its port yet, is read
  again on the next poll. A gateway that restarts reads every instance once, so an instance an
  earlier run answered is a renewal (GD8).

The poll does not wait for its handlers: a container still handled from an earlier poll, such as one
whose publication the gateway is still saving, is skipped, so one slow container never holds the
others' renewals. A handler never faults the channel: a renewal the gateway fails with any exception
is not recorded as handled, so the next poll hands it over again. Stopping the channel waits for the
handlers still running. A publication the gateway accepted is reported to the infrastructure once the
instance has its answer, which is how an update learns its containers are ready (GD5).

The renewal is a count rather than a time because only the gateway compares it, and only for change.

**GD3 — Calls go over the same port.** A read is `GET actions/{name}`, a write `POST actions/{name}`, a
restore `PUT state/{name}`, at `http://127.0.0.1:{host port}/`. The control-plane endpoint an instance
publishes, `docker:///networks/{network}/containers/{container}:{port}/`, names the container and the
port inside it; the client inspects the container on every call for the host port the engine
published that port on, because Docker can publish it elsewhere after the container restarts. The
client refuses any other form. A control plane answers every outcome with a result (200, 400, 404, or
405); any other status, such as the 503 of an instance whose control plane is not open yet, or a body
that is not a result, is no answer and throws `HttpRequestException`.

**GD4 — One network per gateway, and what runs there.** Deploying a resource creates:

- the network (`DockerInfrastructureOptions.Network`, default `cohesion`), a bridge labelled
  `com.assimalign.cohesion.managed-by=cohesion`, once;
- one container per replica of the resource's image, joined to the network with the resource's name
  as its alias, labelled with the network, the provider, the resource, the deployment, the revision,
  the workload (GD5), and its replica number, and receiving the runtime contract as its environment
  exactly as Kubernetes hands it (GK4): `COHESION_APPLICATION`, `COHESION_GATEWAY=docker`,
  `COHESION_RESOURCE`, `COHESION_MANIFEST_ENDPOINT=docker:///networks/{network}`,
  `COHESION_DEPLOYMENT`, the `http` endpoint on `0.0.0.0:EndpointPort`, `COHESION_ENVIRONMENT` when
  the gateway has one, every setting as a `COHESION_CONFIG__` variable, plus the container's own name
  (`DOCKER_CONTAINER_NAME`) and control-plane port (`DOCKER_CONTROL_PLANE_PORT`);
- both of the container's ports published on ephemeral ports of `127.0.0.1`: the control-plane port
  for the channel (GD1), and the `http` port for a developer on the host, and the end-to-end test.

The resource's `endpoint` output is `http://{name}:{EndpointPort}`: its alias on the network, where
every container of the resource answers, so a dependent's setting reaches it by the engine's DNS.

**GD5 — A workload decides an update; the revision is what the model does not carry (2026-10-31).**
The provider contract (`PlanResourceAsync`) says the revision fingerprints what the provider deploys
that the model does not carry and leaves out the settings' values and the extensions, because the
gateway treats a new revision as a new definition, which starts without the state recorded for the
previous one. So two hashes, each the first 16 hex digits of a SHA-256:

- the **revision**: the platform's template version (`DockerWorkload.TemplateVersion`, 1), the image,
  and both ports. `PlanResourceAsync` returns it.
- the **workload**: the revision, the replica count, the environment name the containers receive
  (`COHESION_ENVIRONMENT`, copied from the gateway's own), and the settings in ordinal order, each
  value prefixed with its length as the gateway's deployment id is, so no two sets of values hash
  alike whatever they contain. It is a label on every container, and it decides the plan: no
  container of the resource is a create; as many containers as replicas, all of the workload, all
  `running`, and none in a restart loop (fewer than two restarts, GD7) is nothing; anything else is an
  update. A stopped or crashing container of the workload is therefore an update of the same
  deployment id, which starts it again and lets the watch report why it does not run, rather than a
  plan that marks the resource ready with nothing watched.

Kubernetes keeps the same split (Gateway.Kubernetes GK5). Containers are named
`{network}-{resource}-{workload[..8]}-{replica}`, so an update's containers never collide with the
ones they replace. A container of the same name from an attempt that did not finish is started again
when it is the same deployment's, recreated when the engine counted it as a restart loop (so one that
recovered is not reported as one that keeps ending), and replaced when it is another deployment's.

An update keeps the previous containers until every one of its own has published, as a Deployment
keeps its old pods until the new ones are ready: they keep running, and answering under the
resource's name, alongside the new ones. The infrastructure records the update's containers before
it starts them; the channel reports each accepted publication (GD2), and the last one removes every
other container of the resource. An update whose containers never publish (a bad setting or image)
therefore leaves the previous containers serving, and the next plan sees the mix and updates again.
The removal is best effort: one that fails leaves the containers, and the next run's update removes
them once its containers publish again. Until then the alias round-robins between both workloads.

**GD6 — The program is never PID 1.** Every container runs under the engine's init
(`HostConfig.Init`: `docker-init` on Docker, `catatonit` on Podman), so the program is PID 2. A .NET
program that is PID 1 cannot end itself with a default-action signal: one whose start fails with an
unhandled exception calls `abort()`, which PID 1 ignores, and on arm64 glibc then spins. Observed with
the fixture image under Podman 5.8.6 linux/arm64 on 2026-10-31: without an init the container stayed
`Up` with the program at 78 % CPU; under the init the same image exited with 134 at once. The same
finding made Kubernetes share each pod's process namespace (GK6).

**GD7 — The engine restarts a container, and the watch reports one that keeps ending.** Containers run
with the restart policy `unless-stopped`, as a Deployment restarts its pods. Watching a deployment
lists its containers every second and fails the step at once when one has `exited` or is `dead`, has
restarted twice (a restart loop), or was `created` with an engine error such as a missing executable.
The failure names the container and the engine's reason, then the last 20 lines the program wrote,
so an unhandled exception reaches the gateway's error. While the engine restarts a container, it
reports the exit code of the current run, not the last, so the reason gives the code only when it is
not 0. The watch ends when the deployment's containers are gone: a later deployment replaced them or a
teardown removed them.

**GD8 — Workloads outlive the gateway.** A gateway that stops leaves its containers running; one that
runs again over the same network adopts them: its plan reads the same workload, running and healthy,
and changes nothing, and the instances, which it does not know, renew, are sent to publish again
(GD2), and register. Containers someone stopped, or that crash, are not adopted as they are (GD5).
Tearing a resource down removes its containers; the network stays, as the Kubernetes namespace does.

**GD9 — The engine is resolved as the docker CLI resolves it**, and spoken to directly. The options'
`Endpoint`, then `DOCKER_HOST`, then the platform's default: `npipe:////./pipe/docker_engine` on
Windows, which a Podman machine serves too, and `unix:///var/run/docker.sock` elsewhere (Podman's is
`unix:///run/podman/podman.sock`, rootful). `tcp://` is plain HTTP; TLS and SSH are refused (open
question 2). `DockerEngineClient` is the BCL's `HttpClient` over a Unix socket, a named pipe, or TCP,
with source-generated JSON, so the package takes no Docker SDK; it negotiates the highest API version
both sides speak, from 1.41 (Podman's compatibility API) to 1.51.

**GD10 — `replicas` is the platform's one extension so far, under its own verb.** A JSON number of at
least 1 on the `docker` platform, written by `WithDockerReplicas(int)`; without it a resource runs one
container. The verb is not `WithReplicas`, Kubernetes' name for its own: a gateway program that may run
on either platform declares both counts (the gateway hands each provider only its platform's
extensions, ApplicationModel.Gateway G25), and two `WithReplicas` members would be ambiguous there.

**GD11 — An image per resource, pulled by policy.** `AddImage(provider, name, image)` binds a resource
to the image that runs it, as on Kubernetes. `ImagePullPolicy` is `IfNotPresent` (pull when the engine
has none by that reference), `Always`, or `Never`. A pull names one tag: the reference's own, or `latest` for a reference with
neither a tag nor a digest, as the docker CLI pulls (the Engine API pulls every tag of a repository
named without one). An inspection puts the reference in the path as the docker CLI does, with its
registry colon, slashes, and tag colon literal. The image must be built for the engine's
architecture: a Podman machine on an arm64 host is linux/arm64.

**GD12 — The gateway owns the infrastructure its verb builds (2026-11-01).** ApplicationModel.Gateway
lets a platform's verb hand the gateway a factory instead of an object (`UsePlatform`, G42), so the
platform has two registration verbs:

- `builder.UseDocker(configure)` takes a callback over a `DockerInfrastructureBuilder`, whose
  `Options` are the `DockerInfrastructureOptions` and whose `AddImage` binds an image as the
  infrastructure's does. The gateway's `Build()` calls the callback and the builder's `Build()` last,
  after everything else it checks has passed: a gateway that is never built creates no infrastructure,
  and an invalid network or a duplicate image fails the gateway's build. The gateway owns the
  infrastructure and disposes it when it is disposed, after its run has stopped.
- `builder.UseDocker(infrastructure)` stays for an infrastructure the program creates itself. The
  gateway borrows it, and the program disposes it after the gateway stops.

The builder checks each image as it is added, with the infrastructure's own rules, so a duplicate
fails at the call that adds it and binding the images to the new infrastructure cannot fail. Its
`Build()` creates the infrastructure through `Create`, which copies the options, so each call
creates an independent infrastructure and later changes to `Options` change nothing.

- *Rejected:* creating the infrastructure in `UseDocker`. A builder that is never built, or whose
  model breaks a rule, would then leave an open engine client the program has no handle to dispose.
- *Rejected:* an optional callback. Unlike the in-process platform, nothing runs on Docker
  without an image bound, so `UseDocker()` with no callback would build a gateway that cannot
  deploy anything.

## Open questions

1. **The channel and the control plane have no authentication** (Hosting.Docker HD5). Publishing on
   loopback keeps the ports off other machines, but any process on the engine's host, and any
   container on the network (port 9090 directly), can read an instance's state, answer its
   publication, or call its actions. A per-container secret the gateway sets in the container's
   environment and sends with every request would close both.
2. **Remote engines.** Ports are published on the engine's loopback, so a gateway reaches them only on
   the engine's host. A `tcp://` engine elsewhere, and TLS (`DOCKER_TLS_VERIFY`) or SSH endpoints,
   need a reachable publish address and those transports.
3. **Rollout.** An update keeps the previous containers until all of its own published (GD5), but
   runs every new replica at once, beside every old one: there is no surge limit, and the alias
   serves both workloads until the last new container publishes. A rollout one replica at a time
   would need the gateway to wait for it, as Kubernetes does.
4. **Readiness and liveness.** Containers have no health check. Kubernetes probes the `http` port
   (GK10); Docker could do the same with a `HEALTHCHECK`, which the fixture image cannot run without a
   probe binary, or the gateway could probe the published port.
5. **Polling.** Every second the channel lists the network's containers and reads each one's state.
   Cheap for a developer's machine; an events stream (`/events`) could replace the listing.
6. **Image architecture**, as Kubernetes open question 3.

## Diagnostics

The package raises no events yet: it has no `EventSource`.
