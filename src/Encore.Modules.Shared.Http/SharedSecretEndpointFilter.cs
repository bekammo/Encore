using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Encore.Modules.Shared.Http;

/// <summary>
/// A configured secret in a header: a floor until Identity or mTLS exists, and the routes would
/// not change when one does (018, 029). One refusal for every way of failing, so a caller learns
/// nothing about which.
/// </summary>
public sealed class SharedSecretEndpointFilter : IEndpointFilter
{
    private readonly string _headerName;
    private readonly byte[] _expectedHash;
    private readonly string _title;
    private readonly string _detail;
    private readonly string _reason;

    public SharedSecretEndpointFilter(
        string headerName,
        string expected,
        string title,
        string detail,
        string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);

        _headerName = headerName;
        _expectedHash = Hash(expected);
        _title = title;
        _detail = detail;
        _reason = reason;
    }

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var header = context.HttpContext.Request.Headers[_headerName];

        if (header.Count is not 1 || header[0] is not { } candidate || !Matches(candidate))
        {
            return TypedResults.Problem(
                detail: _detail,
                statusCode: StatusCodes.Status401Unauthorized,
                title: _title,
                instance: context.HttpContext.Request.Path,
                extensions: new Dictionary<string, object?> { ["reason"] = _reason });
        }

        return await next(context).ConfigureAwait(false);
    }

    // Both sides hashed first: FixedTimeEquals returns early on unequal lengths, which would
    // leak the secret's length.
    private bool Matches(string candidate) =>
        CryptographicOperations.FixedTimeEquals(Hash(candidate), _expectedHash);

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
