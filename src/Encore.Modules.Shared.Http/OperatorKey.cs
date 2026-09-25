using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Modules.Shared.Http;

/// <summary>
/// Guards the operator's writes: venues, events and seat maps (030). A module asks for the filter
/// when it maps those routes, so a host that serves them without a key fails at startup rather
/// than serving them open.
/// </summary>
public static class OperatorKey
{
    public const string HeaderName = "X-Operator-Key";

    public const string ConfigurationKey = "Operator:ApiKey";

    public static SharedSecretEndpointFilter Filter(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var key = endpoints.ServiceProvider.GetRequiredService<IConfiguration>()[ConfigurationKey];

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} must be set to map the operator routes. "
                + "It is the only thing standing between anyone and creating venues, events and seats.");
        }

        return new SharedSecretEndpointFilter(
            HeaderName,
            key,
            title: "Operator key required",
            detail: $"The {HeaderName} header is required and must be the configured operator key.",
            reason: "operator_key_invalid");
    }
}
