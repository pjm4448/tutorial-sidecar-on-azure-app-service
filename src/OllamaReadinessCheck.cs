using System.Net.Http.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

public sealed class OllamaReadinessCheck(IHttpClientFactory factory, IOptions<OllamaOptions> options)
    : IHealthCheck
{
    private sealed record TagsResponse(List<ModelTag> Models);
    private sealed record ModelTag(string Name);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var tags = await factory.CreateClient("ollama-probe")
                .GetFromJsonAsync<TagsResponse>("/api/tags", cancellationToken: ct);

            var model = options.Value.DefaultModel;
            var present = tags?.Models?.Any(candidate =>
                string.Equals(candidate.Name, model, StringComparison.Ordinal)) == true;

            return present
                ? HealthCheckResult.Healthy($"Sidecar reachable; {model} present")
                : HealthCheckResult.Unhealthy($"Sidecar reachable; {model} missing");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Sidecar unreachable on loopback", ex);
        }
    }
}