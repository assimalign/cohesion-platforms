using System;
using System.Globalization;
using System.Net;
using System.Net.Http;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

/// <summary>
/// A Docker Engine API call that failed, with what the engine answered.
/// </summary>
internal sealed class DockerEngineException : HttpRequestException
{
    public DockerEngineException(HttpMethod method, Uri? requestUri, HttpStatusCode? statusCode, string? detail, Exception? innerException = null)
        : base(CreateMessage(method, requestUri, statusCode, detail), innerException, statusCode)
    {
    }

    private static string CreateMessage(HttpMethod method, Uri? requestUri, HttpStatusCode? statusCode, string? detail)
    {
        string message = statusCode is { } status
            ? string.Format(CultureInfo.CurrentCulture, ErrorMessages.EngineRequestFailed, method.Method, requestUri?.PathAndQuery, (int)status)
            : string.Format(CultureInfo.CurrentCulture, ErrorMessages.EngineRequestUnanswered, method.Method, requestUri?.PathAndQuery);

        return string.IsNullOrWhiteSpace(detail) ? message : string.Concat(message, " ", detail.Trim());
    }
}
