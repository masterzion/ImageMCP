using ImageMCP.Models;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ImageMCP.Services;

/// <summary>
/// Client for interacting with ComfyUI API
/// </summary>
public class ComfyUIClient : IDisposable
{
    private readonly ILogger<ComfyUIClient> _logger;
    private readonly ComfyUISettings _settings;
    private readonly HttpClient _httpClient;
    private ClientWebSocket? _webSocket;
    private readonly string _clientId;
    private bool _disposed;

    public ComfyUIClient(ComfyUISettings settings, ILogger<ComfyUIClient> logger)
    {
        _settings = settings;
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds) };
        _clientId = Guid.NewGuid().ToString();
    }

    /// <summary>
    /// Connect to ComfyUI WebSocket
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            _logger.LogDebug("WebSocket already connected");
            return;
        }

        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();

        var wsEndpoint = GetWebSocketEndpoint();
        _logger.LogInformation("Connecting to ComfyUI WebSocket: {Endpoint}", wsEndpoint);

        try
        {
            await _webSocket.ConnectAsync(new Uri(wsEndpoint), cancellationToken);
            _logger.LogInformation("Connected to ComfyUI WebSocket with client ID: {ClientId}", _clientId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to connect to ComfyUI WebSocket");
            throw new InvalidOperationException($"Could not connect to ComfyUI at {wsEndpoint}", ex);
        }
    }

    /// <summary>
    /// Submit a workflow to ComfyUI
    /// </summary>
    public async Task<string> SubmitWorkflowAsync(
        string workflowJson, 
        CancellationToken cancellationToken = default)
    {
        var httpEndpoint = GetHttpEndpoint();
        var url = $"{httpEndpoint}/prompt";

        // Build the request with the workflow JSON and client ID
        var requestJson = $$"""
        {
            "prompt": {{workflowJson}},
            "client_id": "{{_clientId}}"
        }
        """;

        _logger.LogDebug("Submitting workflow to ComfyUI: {Url}", url);

        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.PostAsync(url, content, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("ComfyUI returned error: {StatusCode} - {Response}", 
                    response.StatusCode, responseText);
                throw new InvalidOperationException(
                    $"ComfyUI returned HTTP {response.StatusCode}: {responseText}");
            }

            var promptResponse = JsonSerializer.Deserialize<ComfyPromptResponse>(responseText);
            
            if (promptResponse == null || string.IsNullOrEmpty(promptResponse.PromptId))
            {
                throw new InvalidOperationException("Invalid response from ComfyUI: missing prompt_id");
            }

            _logger.LogInformation("Workflow submitted successfully. Prompt ID: {PromptId}", 
                promptResponse.PromptId);

            return promptResponse.PromptId;
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error submitting workflow to ComfyUI");
            throw new InvalidOperationException($"Failed to submit workflow to ComfyUI at {url}", ex);
        }
    }

    /// <summary>
    /// Wait for completion, using history as a fallback for missed WebSocket events.
    /// </summary>
    public async Task<bool> WaitForCompletionAsync(
        string promptId, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Waiting for prompt completion: {PromptId}", promptId);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        var token = timeoutCts.Token;
        Task<bool>? socketTask = null;
        try
        {
            // Poll history even while the WebSocket connection is being established.
            socketTask = MonitorExecutionAsync(promptId, token);

            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (await IsHistoryCompleteAsync(promptId, token))
                {
                    return true;
                }
                var delay = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _settings.PollIntervalSeconds)), token);
                if (socketTask != null && await Task.WhenAny(socketTask, delay) == socketTask)
                {
                    if (await socketTask)
                    {
                        return true;
                    }
                    // History remains available after the socket disconnects.
                    socketTask = null;
                }
                await delay;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Workflow execution timed out after {_settings.TimeoutSeconds} seconds");
        }
        finally
        {
            timeoutCts.Cancel();
            if (socketTask != null)
            {
                try
                {
                    await socketTask;
                }
                catch (Exception)
                {
                    // Observe the receiver after cancellation; the result was handled above.
                }
            }
        }
    }

    private async Task<bool> IsHistoryCompleteAsync(string promptId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"{GetHttpEndpoint()}/history/{Uri.EscapeDataString(promptId)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        var history = JsonSerializer.Deserialize<Dictionary<string, ComfyHistoryResponse>>(
            await response.Content.ReadAsStringAsync(cancellationToken));
        if (history == null || !history.TryGetValue(promptId, out var entry) || !entry.Status.HasValue)
        {
            return false;
        }

        var status = entry.Status.Value;
        if (status.TryGetProperty("status_str", out var statusName) && statusName.GetString() == "error")
        {
            throw new InvalidOperationException($"Workflow execution failed: {status.GetRawText()}");
        }
        return status.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.True;
    }

    private async Task<bool> MonitorExecutionAsync(string promptId, CancellationToken cancellationToken)
    {
        try
        {
            await ConnectAsync(cancellationToken);
        }
        catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("WebSocket unavailable; monitoring history for {PromptId}", promptId);
            return false;
        }
        var buffer = new byte[4096];
        try
        {
            while (_webSocket!.State == WebSocketState.Open)
            {
                using var messageBuffer = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return false;
                    }
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        messageBuffer.Write(buffer, 0, result.Count);
                    }
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }
                var message = JsonSerializer.Deserialize<ComfyWebSocketMessage>(messageBuffer.ToArray());
                if (message?.Data is not JsonElement data ||
                    !data.TryGetProperty("prompt_id", out var id) || id.GetString() != promptId)
                {
                    continue;
                }

                if (message.Type is "execution_error" or "execution_interrupted")
                {
                    throw new InvalidOperationException($"Workflow execution failed: {data.GetRawText()}");
                }
                if (message.Type == "execution_success" ||
                    (message.Type == "executing" && data.TryGetProperty("node", out var node) && node.ValueKind == JsonValueKind.Null))
                {
                    _logger.LogInformation("Workflow execution completed: {PromptId}", promptId);
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is WebSocketException or JsonException)
        {
            _logger.LogWarning(ex, "WebSocket monitoring stopped; using history for {PromptId}", promptId);
            return false;
        }
    }

    /// <summary>
    /// Get generated images from ComfyUI
    /// </summary>
    public async Task<List<byte[]>> GetImagesAsync(
        string promptId, 
        CancellationToken cancellationToken = default)
    {
        var images = await GetGeneratedImagesAsync(promptId, cancellationToken);
        return images.Select(image => image.Data).ToList();
    }

    public async Task<List<GeneratedImage>> GetGeneratedImagesAsync(
        string promptId,
        CancellationToken cancellationToken = default)
    {
        var httpEndpoint = GetHttpEndpoint();
        var url = $"{httpEndpoint}/history/{promptId}";

        _logger.LogDebug("Fetching images from history: {Url}", url);

        // Retry logic - sometimes history takes a moment to be available
        const int maxRetries = 10;
        const int retryDelayMs = 1000;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    _logger.LogInformation("Retry attempt {Attempt}/{MaxRetries} for history: {PromptId}", attempt + 1, maxRetries, promptId);
                    await Task.Delay(retryDelayMs, cancellationToken);
                }

                using var response = await _httpClient.GetAsync(url, cancellationToken);
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"Failed to get history: HTTP {response.StatusCode}");
                }

                var history = JsonSerializer.Deserialize<Dictionary<string, ComfyHistoryResponse>>(responseText);
                
                if (history == null || !history.ContainsKey(promptId))
                {
                    if (attempt < maxRetries - 1)
                    {
                        _logger.LogInformation("History not yet available for prompt: {PromptId}, retrying in {Delay}ms...", promptId, retryDelayMs);
                        continue;
                    }
                    
                    _logger.LogWarning("No history found for prompt after {Attempts} attempts: {PromptId}", maxRetries, promptId);
                    return new List<GeneratedImage>();
                }

                var promptHistory = history[promptId];
                var images = new List<GeneratedImage>();

                if (promptHistory.Outputs == null)
                {
                    _logger.LogWarning("No outputs in history for prompt: {PromptId}", promptId);
                    return images;
                }

                // Find SaveImage nodes in outputs
                foreach (var output in promptHistory.Outputs.Values)
                {
                    if (output.Images != null)
                    {
                        foreach (var imageInfo in output.Images)
                        {
                            var imageData = await DownloadImageAsync(imageInfo, cancellationToken);
                            if (imageData != null)
                            {
                                images.Add(new GeneratedImage(imageData, GetImageUrl(imageInfo)));
                            }
                        }
                    }
                }

                _logger.LogInformation("Retrieved {Count} images for prompt: {PromptId}", images.Count, promptId);
                return images;
            }
            catch (Exception ex) when (ex is not InvalidOperationException && ex is not OperationCanceledException && attempt < maxRetries - 1)
            {
                _logger.LogDebug(ex, "Error fetching images (attempt {Attempt}/{MaxRetries}), retrying...", attempt + 1, maxRetries);
            }
        }

        throw new InvalidOperationException("Failed to retrieve images from ComfyUI after multiple attempts");
    }

    /// <summary>
    /// Download a specific image from ComfyUI
    /// </summary>
    private async Task<byte[]?> DownloadImageAsync(
        ComfyImageInfo imageInfo, 
        CancellationToken cancellationToken)
    {
        var url = GetImageUrl(imageInfo);

        _logger.LogDebug("Downloading image: {Filename}", imageInfo.Filename);

        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to download image: {Filename}", imageInfo.Filename);
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error downloading image: {Filename}", imageInfo.Filename);
            return null;
        }
    }

    private string GetImageUrl(ComfyImageInfo imageInfo) =>
        $"{GetHttpEndpoint()}/view?filename={Uri.EscapeDataString(imageInfo.Filename)}&subfolder={Uri.EscapeDataString(imageInfo.Subfolder)}&type={Uri.EscapeDataString(imageInfo.Type)}";

    private string GetHttpEndpoint()
    {
        // Convert ws:// to http:// or wss:// to https://
        var endpoint = _settings.ApiEndpoint
            .Replace("ws://", "http://")
            .Replace("wss://", "https://");
        
        return endpoint.TrimEnd('/');
    }

    private string GetWebSocketEndpoint()
    {
        var endpoint = _settings.ApiEndpoint.TrimEnd('/');
        
        // Ensure it's a WebSocket URL
        if (!endpoint.StartsWith("ws://") && !endpoint.StartsWith("wss://"))
        {
            endpoint = endpoint.Replace("http://", "ws://").Replace("https://", "wss://");
        }

        return $"{endpoint}/ws?clientId={_clientId}";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _webSocket?.Dispose();
            _httpClient?.Dispose();
            _disposed = true;
        }
    }
}
