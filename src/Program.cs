using System.Threading.RateLimiting;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

var builder = WebApplication.CreateBuilder(args);

if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
	builder.Services.AddOpenTelemetry().UseAzureMonitor();
}

builder.Services.AddProblemDetails();

builder.Services.AddOptions<OllamaOptions>()
	.Bind(builder.Configuration.GetSection(OllamaOptions.Section))
	.ValidateDataAnnotations()
	.Validate(options => options.AllowedModels.Contains(options.DefaultModel, StringComparer.Ordinal),
		"Ollama:DefaultModel must be listed in Ollama:AllowedModels.")
	.ValidateOnStart();

builder.Services.AddOptions<ApiKeyOptions>()
	.Bind(builder.Configuration.GetSection(ApiKeyOptions.Section))
	.ValidateDataAnnotations()
	.ValidateOnStart();

builder.Services.AddHttpClient<OllamaClient>((serviceProvider, client) =>
{
	var options = serviceProvider.GetRequiredService<IOptions<OllamaOptions>>().Value;
	client.BaseAddress = new Uri(options.BaseUrl);
	client.Timeout = Timeout.InfiniteTimeSpan;
})
.AddStandardResilienceHandler(options =>
{
	var attemptSeconds = builder.Configuration.GetValue("Inference:AttemptTimeoutSeconds", 190);
	var totalSeconds = builder.Configuration.GetValue("Inference:TotalTimeoutSeconds", 200);
	if (attemptSeconds < 1 || totalSeconds <= attemptSeconds || totalSeconds > 200)
		throw new InvalidOperationException("Require 1 <= attempt < total <= 200 seconds.");

	options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(attemptSeconds);
	options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(totalSeconds);
	options.Retry.DisableForUnsafeHttpMethods();
	options.Retry.Delay = TimeSpan.FromSeconds(2);
	options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(2 * attemptSeconds);
	options.CircuitBreaker.FailureRatio = 0.5;
	options.CircuitBreaker.MinimumThroughput = 5;
	options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("ollama-probe", (serviceProvider, client) =>
{
	var options = serviceProvider.GetRequiredService<IOptions<OllamaOptions>>().Value;
	client.BaseAddress = new Uri(options.BaseUrl);
	client.Timeout = TimeSpan.FromSeconds(3);
});

builder.Services.AddHealthChecks()
	.AddCheck<OllamaReadinessCheck>("ollama", tags: ["ready"]);

builder.Services.AddRateLimiter(options =>
{
	options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
	options.OnRejected = async (context, cancellationToken) =>
	{
		context.HttpContext.Response.Headers.RetryAfter = "5";
		await Results.Problem(
			statusCode: StatusCodes.Status429TooManyRequests,
			title: "Inference capacity is busy. Retry later.")
			.ExecuteAsync(context.HttpContext);
	};
	options.AddConcurrencyLimiter("inference", limiterOptions =>
	{
		limiterOptions.PermitLimit = 1;
		limiterOptions.QueueLimit = 0;
		limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
	});
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseWhen(
	context => context.Request.Path.StartsWithSegments("/api"),
	branch => branch.UseMiddleware<ApiKeyMiddleware>());

app.UseRateLimiter();

app.MapHealthChecks("/healthz/live", new HealthCheckOptions
{
	Predicate = _ => false,
	ResponseWriter = WriteHealthAsync
});

app.MapHealthChecks("/healthz", new HealthCheckOptions
{
	Predicate = registration => registration.Tags.Contains("ready"),
	ResponseWriter = WriteHealthAsync
});

app.MapPost("/api/generate-poem", async (
		PoemRequest input,
		OllamaClient ollama,
		IOptions<OllamaOptions> options,
		ILogger<Program> logger,
		CancellationToken ct) =>
	{
		if (string.IsNullOrWhiteSpace(input.Subject) || input.Subject.Length > 200)
			return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
				title: "subject is required (max 200 characters).");

		var model = string.IsNullOrWhiteSpace(input.Model) ? options.Value.DefaultModel : input.Model;
		if (!options.Value.AllowedModels.Contains(model, StringComparer.Ordinal))
			return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
				title: $"model '{model}' is not permitted.");

		try
		{
			var poem = await ollama.GenerateAsync(
				model,
				$"Write a short four-line poem about {input.Subject}.",
				options.Value.MaxTokens,
				ct);

			return Results.Ok(new { subject = input.Subject, model, poem });
		}
		catch (BrokenCircuitException)
		{
			return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
				title: "Inference sidecar temporarily unavailable.");
		}
		catch (TimeoutRejectedException)
		{
			return Results.Problem(statusCode: StatusCodes.Status504GatewayTimeout,
				title: "Inference timed out.");
		}
		catch (HttpRequestException ex)
		{
			logger.LogError(ex, "Sidecar call failed (automatic inference retries disabled)");
			return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
				title: "Inference sidecar returned an error.");
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			logger.LogInformation("Client disconnected before the poem was generated");
			return Results.Empty;
		}
	})
	.RequireRateLimiting("inference");

app.Run();

static Task WriteHealthAsync(HttpContext context, HealthReport report) =>
	context.Response.WriteAsJsonAsync(new
	{
		status = report.Status.ToString(),
		checks = report.Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Description)
	});

public partial class Program { }
