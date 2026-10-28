using System.Net;
using System.Net.Http;

namespace Pinboard.App.Services.AiSessions;

public enum SessionFailureKind { Transient, MissingSource, Authorization, Quota, Permission, Review }

public static class SessionRetryPolicy
{
    public static SessionFailureKind Classify(Exception error) => error switch
    {
        DriveAuthorizationException => SessionFailureKind.Authorization,
        DriveQuotaException => SessionFailureKind.Quota,
        FileNotFoundException or DirectoryNotFoundException => SessionFailureKind.MissingSource,
        UnauthorizedAccessException => SessionFailureKind.Permission,
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 } => SessionFailureKind.Transient,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => SessionFailureKind.Authorization,
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => SessionFailureKind.Permission,
        HttpRequestException { StatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError } request
            when request.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests) => SessionFailureKind.Review,
        HttpRequestException or IOException or TimeoutException or TaskCanceledException => SessionFailureKind.Transient,
        _ => SessionFailureKind.Review
    };
    public static TimeSpan Delay(int attempts) => TimeSpan.FromSeconds(Math.Min(1800, 30 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 6))));
    public static string Status(SessionFailureKind kind) => kind switch
    {
        SessionFailureKind.MissingSource => "Needs source", SessionFailureKind.Authorization => "Sign-in required",
        SessionFailureKind.Quota => "Storage full", SessionFailureKind.Permission => "Access required",
        SessionFailureKind.Review => "Needs review", _ => "Retry pending"
    };
}
