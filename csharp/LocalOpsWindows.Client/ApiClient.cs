using System.Net.Http.Json;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LocalOpsWindows.Client;

public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public ApiClient(int port, HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        _http.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
    }

    public async Task<DashboardState> GetStateAsync(CancellationToken cancellationToken)
        => await GetAsync<DashboardState>("api/state", cancellationToken) ?? new DashboardState();

    public async Task<HealthSnapshot> GetHealthAsync(CancellationToken cancellationToken)
        => await GetAsync<HealthSnapshot>("api/health", cancellationToken) ?? new HealthSnapshot();

    public async Task<string> GetLogAsync(string appId, CancellationToken cancellationToken)
    {
        var endpoint = appId == "console"
            ? "api/console/log?tail=300"
            : $"api/apps/{appId}/logs?tail=300";
        using var response = await _http.GetAsync(endpoint, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
    }

    public async Task<JsonDocument> GetJsonAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(endpoint, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public Task<JsonDocument?> PostAsync(string endpoint, object payload, CancellationToken cancellationToken)
        => SendJsonAsync(HttpMethod.Post, endpoint, payload, cancellationToken);

    public Task<JsonDocument?> PutAsync(string endpoint, object payload, CancellationToken cancellationToken)
        => SendJsonAsync(HttpMethod.Put, endpoint, payload, cancellationToken);

    public Task<JsonDocument?> DeleteAsync(string endpoint, CancellationToken cancellationToken)
        => SendJsonAsync(HttpMethod.Delete, endpoint, null, cancellationToken);

    internal async Task<byte[]> GetIconAsync(string path, CancellationToken cancellationToken)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"^/icons/(?:fav-)?[0-9a-fA-F]{8}\.(?:png|jpg|jpeg|webp|ico)$"))
            throw new InvalidOperationException("图标路径无效。");
        using var response = await _http.GetAsync(path.TrimStart('/'), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 5 * 1024 * 1024)
            throw new InvalidOperationException("图标不能超过 5MB。");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + count > 5 * 1024 * 1024) throw new InvalidOperationException("图标不能超过 5MB。");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return output.ToArray();
    }

    internal async Task UploadIconAsync(string id, byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length is 0 or > 5 * 1024 * 1024) throw new InvalidOperationException("图标大小无效。");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/apps/{id}/icon")
        {
            Content = new ByteArrayContent(bytes)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        using var response = await _http.SendAsync(request, cancellationToken);
        using var result = await ReadMutationAsync(response, cancellationToken);
    }

    public async Task<T?> GetAsync<T>(string endpoint, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(endpoint, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(_json, cancellationToken);
    }

    private async Task<JsonDocument?> SendJsonAsync(
        HttpMethod method, string endpoint, object? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, endpoint);
        if (payload is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        return await ReadMutationAsync(response, cancellationToken);
    }

    private static async Task<JsonDocument> ReadMutationAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = document.RootElement;
        var rejected = root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False;
        if (!response.IsSuccessStatusCode || rejected)
        {
            var error = root.TryGetProperty("error", out var errorValue)
                ? errorValue.GetString()
                : $"请求失败（{(int)response.StatusCode}）";
            throw new ApiException(error ?? "请求失败", root.TryGetProperty("requiresStop", out var stop)
                && stop.ValueKind == JsonValueKind.True);
        }

        return JsonDocument.Parse(document.RootElement.GetRawText());
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class ApiException(string message, bool requiresStop) : InvalidOperationException(message)
{
    internal bool RequiresStop { get; } = requiresStop;
}
