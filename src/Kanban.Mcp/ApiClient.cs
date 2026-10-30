using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kanban.Mcp;

public sealed class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
}

/// <summary>Thin client for the local Kanban REST API hosted by the desktop app.</summary>
public sealed class ApiClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public ApiClient()
    {
        var (baseUrl, token, actor) = LoadConfig();
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Add("X-API-Key", token);
        _http.DefaultRequestHeaders.Add("X-Actor", actor);
    }

    private static (string BaseUrl, string Token, string Actor) LoadConfig()
    {
        var envUrl = Environment.GetEnvironmentVariable("KANBAN_API_URL");
        var envKey = Environment.GetEnvironmentVariable("KANBAN_API_KEY");
        var actor = Environment.GetEnvironmentVariable("KANBAN_ACTOR") ?? "agent:mcp";
        if (!string.IsNullOrEmpty(envUrl))
            return (envUrl, envKey ?? "", actor);

        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kanban", "api.json");
        if (File.Exists(configPath))
        {
            try
            {
                var doc = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
                var port = doc?["port"]?.GetValue<int>() ?? 37650;
                var token = doc?["token"]?.GetValue<string>() ?? "";
                return ($"http://127.0.0.1:{port}", token, actor);
            }
            catch (Exception) { /* fall through */ }
        }

        return ("http://127.0.0.1:37650", envKey ?? "", actor);
    }

    public Task<string> Get(string path) => Send(new HttpRequestMessage(HttpMethod.Get, _baseUrl + path));

    public Task<string> Post(string path, JsonNode? body) => Send(WithBody(new HttpRequestMessage(HttpMethod.Post, _baseUrl + path), body));

    public Task<string> Patch(string path, JsonNode? body) => Send(WithBody(new HttpRequestMessage(HttpMethod.Patch, _baseUrl + path), body));

    public Task<string> Delete(string path) => Send(new HttpRequestMessage(HttpMethod.Delete, _baseUrl + path));

    private static HttpRequestMessage WithBody(HttpRequestMessage req, JsonNode? body)
    {
        var json = (body ?? new JsonObject()).ToJsonString();
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return req;
    }

    private async Task<string> Send(HttpRequestMessage req)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req);
        }
        catch (HttpRequestException e)
        {
            throw new ApiException(
                "Kanban デスクトップアプリに接続できません。アプリを起動してから再試行してください。" +
                $" ({e.Message})");
        }

        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            var message = text;
            try
            {
                var node = JsonNode.Parse(text) as JsonObject;
                message = node?["error"]?.GetValue<string>() ?? text;
            }
            catch (JsonException) { }
            throw new ApiException($"API error {(int)resp.StatusCode}: {message}");
        }
        return text;
    }
}
