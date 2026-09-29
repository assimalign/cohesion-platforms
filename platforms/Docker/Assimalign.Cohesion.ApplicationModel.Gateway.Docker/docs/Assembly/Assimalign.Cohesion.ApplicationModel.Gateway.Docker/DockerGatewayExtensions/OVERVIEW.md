# DockerGatewayExtensions

Namespace: `Assimalign.Cohesion.ApplicationModel.Gateway.Docker`

```csharp
public static class DockerGatewayExtensions
```

Provides .NET 10 extension members that select `DockerGateway` on an `IApplicationBuilder`.

## Methods

```csharp
IApplicationBuilder UseDockerGateway();

IApplicationBuilder UseDockerGateway(
    Action<DockerGatewayOptions> configure);

IApplicationBuilder UseDockerGateway(
    string[] args,
    Action<DockerGatewayOptions>? configure = null);
```

Every overload returns the same builder for chaining. The parameterless overload constructs
default options. The configuration overload invokes the required callback before constructing the
gateway.

The command-line overload first applies Cohesion's common `ApplicationGatewayCommandLine`
settings, then calls `DockerGatewayCommandLine.Apply` for Docker-specific arguments, and finally invokes the optional callback. The
callback can therefore refine or replace values parsed from the command line.

## Docker-specific arguments

| Argument | Forms | Effect |
| --- | --- | --- |
| `--docker-host` | `--docker-host URI` or `--docker-host=URI` | Sets `DockerGatewayOptions.EngineEndpoint`; the value must be an absolute URI. |
| `--image-archive` | `--image-archive IMAGE=PATH` or `--image-archive=IMAGE=PATH` | Adds or replaces an `ImageArchives` mapping. The argument is repeatable and is split at its first `=`. |

| `--control-plane-bind` | `--control-plane-bind HOST[:PORT]` or `--control-plane-bind=HTTP-URI` | Sets the HTTP localhost/IP bind address; bare hosts request port zero. |

The Docker-specific parser ignores unrelated arguments after the common parser has had an
opportunity to apply them. Null arrays or callbacks throw `ArgumentNullException`; missing,
empty, malformed, or non-absolute Docker argument values throw `ArgumentException`. Final option
validation occurs when the overload constructs `DockerGateway`.

```csharp
builder.UseDockerGateway(
    args,
    options => options.PublicHost = "dev.example.test");
```

Back to the [namespace overview](../OVERVIEW.md).
