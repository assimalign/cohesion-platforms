using System;
using System.Net;
using System.Net.Http;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

internal sealed class DockerEngineException : HttpRequestException
{
    public DockerEngineException(
        HttpMethod method,
        Uri? requestUri,
        HttpStatusCode statusCode,
        string? responseBody,
        Exception? innerException = null)
        : base(CreateMessage(method, requestUri, statusCode, responseBody), innerException, statusCode)
    {
        Method = method;
        RequestUri = requestUri;
        ResponseBody = responseBody;
    }

    public HttpMethod Method { get; }

    public Uri? RequestUri { get; }

    public string? ResponseBody { get; }

    private static string CreateMessage(
        HttpMethod method,
        Uri? requestUri,
        HttpStatusCode statusCode,
        string? responseBody)
    {
        string location = requestUri?.ToString() ?? "the Docker Engine";
        string message = $"Docker Engine request {method.Method} {location} failed with HTTP {(int)statusCode} ({statusCode}).";

        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            message = $"{message} {responseBody.Trim()}";
        }

        return message;
    }
}
