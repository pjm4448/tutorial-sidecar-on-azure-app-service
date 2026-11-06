using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

public sealed class ApiKeyOptions
{
    public const string Section = "Api";

    [Required, MinLength(32)]
    public string Key { get; set; } = string.Empty;
}

public sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<ApiKeyOptions> options)
{
    private const string HeaderName = "x-api-key";
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(options.Value.Key);

    public async Task InvokeAsync(HttpContext context)
    {
        var provided = Encoding.UTF8.GetBytes(context.Request.Headers[HeaderName].ToString());

        if (!CryptographicOperations.FixedTimeEquals(provided, _expected))
        {
            await Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Missing or invalid API key.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}