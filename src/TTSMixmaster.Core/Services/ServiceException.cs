using System.Net;

namespace TTSMixmaster.Core.Services;

/// <summary>A safe, user-readable music-service failure. Never contains credentials or raw response bodies.</summary>
public sealed class ServiceException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
