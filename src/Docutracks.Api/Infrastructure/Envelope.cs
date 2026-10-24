using System.Net;

namespace Docutracks.Api.Infrastructure;

/// <summary>
/// The response of every endpoint, as the folders API v2: `{ Success, Payload, ErrorInfo }` with
/// status 200, so the client shows `ErrorInfo.ExceptionMessage` as is. Only a missing identity is a 401.
/// </summary>
public sealed record Envelope<T>(bool Success, T? Payload, ErrorInfo? ErrorInfo);

public sealed record ErrorInfo(string ExceptionMessage, string? ErrorCode = null);

public static class Envelope
{
    public static Envelope<T> Ok<T>(T payload) => new(true, payload, null);

    public static Envelope<object> Fail(string message, string? code = null) => new(false, null, new ErrorInfo(message, code));
}

/// <summary>
/// A failure the user should read (Greek), returned as `Success: false`. `Status` other than
/// 200 only for missing identity (401) and calls that cannot be understood (400).
/// </summary>
public class ApiException(string message, HttpStatusCode status = HttpStatusCode.OK, string? code = null) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string? Code { get; } = code;

    public static ApiException NotFound(string message = "Το έγγραφο δεν βρέθηκε ή δεν έχετε αντίγραφό του σε αυτή την υπηρεσία.") =>
        new(message, HttpStatusCode.OK, "not_found");

    public static ApiException BadRequest(string message) => new(message, HttpStatusCode.BadRequest, "bad_request");

    public static ApiException Unauthorized() =>
        new("Η σύνδεση έληξε. Συνδεθείτε ξανά.", HttpStatusCode.Unauthorized, "unauthorized");

    public static ApiException Forbidden(string message = "Δεν έχετε ρόλο σε αυτή την υπηρεσία.") =>
        new(message, HttpStatusCode.OK, "forbidden");
}
