using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

public sealed class OllamaOptions
{
    public const string Section = "Ollama";

    [Required, Url]
    public string BaseUrl { get; set; } = "http://localhost:11434";

    [Required]
    public string DefaultModel { get; set; } = "phi3:mini";

    [MinLength(1)]
    public string[] AllowedModels { get; set; } = [];

    [Range(16, 1024)]
    public int MaxTokens { get; set; } = 256;
}

public sealed record PoemRequest(string? Subject, string? Model);

public sealed record GenerateRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("options")] Dictionary<string, object> Options);

public sealed record GenerateResponse(
    [property: JsonPropertyName("response")] string? Response,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("total_duration")] long TotalDuration = 0,
    [property: JsonPropertyName("load_duration")] long LoadDuration = 0,
    [property: JsonPropertyName("prompt_eval_count")] int PromptTokens = 0,
    [property: JsonPropertyName("eval_count")] int OutputTokens = 0);

public sealed class OllamaClient(HttpClient http)
{
    public async Task<string> GenerateAsync(string model, string prompt, int maxTokens, CancellationToken ct)
    {
        var body = new GenerateRequest(model, prompt, Stream: false,
            Options: new() { ["num_predict"] = maxTokens });
        using var response = await http.PostAsJsonAsync("/api/generate", body, cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        GenerateResponse? result;
        try
        {
            result = await response.Content.ReadFromJsonAsync<GenerateResponse>(cancellationToken: ct);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new HttpRequestException("Invalid JSON from inference sidecar.", ex);
        }

        if (result is null || !result.Done || string.IsNullOrWhiteSpace(result.Response))
            throw new HttpRequestException("Incomplete or empty inference response.");

        return result.Response;
    }
}