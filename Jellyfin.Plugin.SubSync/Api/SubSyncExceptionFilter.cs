using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubSync.Api;

/// <summary>
/// Turns any exception a SubSync endpoint lets escape into the same error body the deliberate refusals use (D10).
/// </summary>
/// <remarks>
/// Without this, a thrown failure reached the client as Jellyfin's generic "Error processing request." with no
/// detail - the page could only say that something went wrong, not what - while a refusal returned a bare string
/// body and a validation failure returned ProblemDetails. Three shapes for one idea meant the page had to guess,
/// and a bug report could not say which member threw. Every failure now answers as
/// <c>{ status, title, detail }</c>, the exception is logged with its stack, and the status is 500 unless the
/// exception names a better one.
/// </remarks>
public class SubSyncExceptionFilter : IExceptionFilter
{
    private readonly ILogger<SubSyncExceptionFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSyncExceptionFilter"/> class.
    /// </summary>
    /// <param name="logger">Plugin logger.</param>
    public SubSyncExceptionFilter(ILogger<SubSyncExceptionFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void OnException(ExceptionContext context)
    {
        var ex = context.Exception;
        var status = ex switch
        {
            ArgumentException => 400,
            InvalidOperationException => 409,
            UnauthorizedAccessException => 403,
            _ => 500
        };

        _logger.LogError(
            ex,
            "SubSync endpoint {Action} failed with {Type}",
            context.ActionDescriptor?.DisplayName ?? "(unknown)",
            ex.GetType().Name);

        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = status,
            Title = status == 500 ? "SubSync request failed" : "Request refused",
            Detail = ex.Message
        })
        {
            StatusCode = status
        };
        context.ExceptionHandled = true;
    }
}
