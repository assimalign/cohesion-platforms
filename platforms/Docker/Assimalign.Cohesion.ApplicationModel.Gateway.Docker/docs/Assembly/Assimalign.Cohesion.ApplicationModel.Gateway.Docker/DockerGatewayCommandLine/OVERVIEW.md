# DockerGatewayCommandLine

Namespace: `Assimalign.Cohesion.ApplicationModel.Gateway.Docker`

```csharp
public static class DockerGatewayCommandLine
{
    public static void Apply(DockerGatewayOptions options, string[] args);
}
```

The static provider hook applies Docker arguments before the generated configure callback.
`UseDockerGateway(args, configure)` calls the same method after the common gateway parser.
The public static shape is the scoped item 37 exception required by generated provider metadata.

| Switch | Value |
| --- | --- |
| `--docker-host` | Absolute Engine URI. |
| `--image-archive` | Repeated digest-reference `=` archive-path mappings; split at the first equals. |
| `--control-plane-bind` | localhost/IP host, host and port, or absolute HTTP URI. Bare hosts use port zero. |

All switches accept `--name=value` and `--name value`. Unknown switches are ignored. Null options
or arguments throw `ArgumentNullException`; missing or malformed recognized values throw
`ArgumentException`. Final Docker options validation also occurs at gateway construction.

Control-plane addresses must be absolute HTTP URIs using localhost or an IP, matching upstream
listener validation. Other URI components do not change the bound host and port.
Non-loopback binds are operator-chosen LAN exposure. Use port zero for application
sets because each application has a distinct shared control-plane listener.

Provider metadata contains the exact method name
`global::Assimalign.Cohesion.ApplicationModel.Gateway.Docker.DockerGatewayCommandLine.Apply`.
