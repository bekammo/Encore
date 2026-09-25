using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Modules.Shared.Http;

/// <summary>
/// A token bucket per client IP address, for the routes a bot would hammer (030). A floor, not a
/// defence: it stops one machine rotating client ids to hold a venue, not a botnet.
/// </summary>
public static class PerIpRateLimiting
{
    public const string SectionName = "RateLimiting:PerIp";

    /// <summary>
    /// Each module registers its own policy name, so two modules can never collide on one. The
    /// host must call <c>UseRateLimiter</c>, or the policy is silently skipped; a host-seam test
    /// checks that it does.
    /// </summary>
    public static IServiceCollection AddPerIpRateLimitPolicy(
        this IServiceCollection services,
        IConfiguration configuration,
        string policyName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        var options = configuration.GetSection(SectionName).Get<PerIpRateLimitOptions>()
            ?? new PerIpRateLimitOptions();

        if (options.PermitsPerSecond < 1 || options.Burst < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}: PermitsPerSecond and Burst must be at least 1.");
        }

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = RejectAsync;

            limiter.AddPolicy(policyName, context => options.Enabled
                ? RateLimitPartition.GetTokenBucketLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = options.Burst,
                        TokensPerPeriod = options.PermitsPerSecond,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    })
                : RateLimitPartition.GetNoLimiter("disabled"));
        });

        return services;
    }

    private static async ValueTask RejectAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            http.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        await TypedResults.Problem(
                detail: "Too many requests from this address. Wait and try again.",
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Rate limited",
                instance: http.Request.Path,
                extensions: new Dictionary<string, object?>
                {
                    ["reason"] = "rate_limited",
                    ["retriable"] = true
                })
            .ExecuteAsync(http)
            .ConfigureAwait(false);
    }
}
