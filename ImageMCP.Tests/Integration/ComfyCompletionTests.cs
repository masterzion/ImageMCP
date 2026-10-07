using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImageMCP.Models;
using ImageMCP.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ImageMCP.Tests.Integration;

public class ComfyCompletionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HttpToolCall_WaitsAndReturnsImagesOrExplicitError(bool hasImages)
    {
        await using var backend = await Backend.StartAsync();
        var historyReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var historyRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = JsonNode.Parse(CompletedHistory())!;
        var outputs = history["test-prompt"]!["outputs"]!;
        if (hasImages)
        {
            outputs["8"] = outputs["9"]!.DeepClone();
        }
        else
        {
            history["test-prompt"]!["outputs"] = new JsonObject();
        }
        var completedHistory = history.ToJsonString();
        backend.History = () =>
        {
            historyRequested.TrySetResult();
            return historyReady.Task.IsCompleted
                ? completedHistory
                : "{}";
        };

        var projectRoot = Path.GetFullPath("../../../..", AppContext.BaseDirectory);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var serverDll = Path.Combine(projectRoot, "bin", configuration, "net10.0", "ImageMCP.dll");
        var template = Path.GetTempFileName();
        await File.WriteAllTextAsync(template, """{"1":{"class_type":"CLIPTextEncode","inputs":{"text":"test image"}}}""");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        listener.Stop();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = projectRoot, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { serverDll, "--listen", serverUrl, "--template", template,
            "--comfyui-endpoint", backend.Url, "--ComfyUI:TimeoutSeconds", "10" })
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        string? responseJson = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new InvalidOperationException(await output + await errors);
                }
                try
                {
                    using var health = await http.GetAsync($"{serverUrl}/health", startup.Token);
                    if (health.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(50, startup.Token);
            }

            var call = http.PostAsJsonAsync($"{serverUrl}/mcp", new
            {
                jsonrpc = "2.0", id = 17, method = "tools/call",
                @params = new { name = "generate_image", arguments = new { prompt = "a fox in a forest" } }
            });
            var first = await Task.WhenAny(historyRequested.Task, call).WaitAsync(TimeSpan.FromSeconds(5));
            if (first == call)
            {
                using var earlyResponse = await call;
                Assert.Fail($"Tool returned before completion: {await earlyResponse.Content.ReadAsStringAsync()}");
            }
            Assert.False(call.IsCompleted);
            historyReady.SetResult();
            using var response = await call;
            response.EnsureSuccessStatusCode();
            responseJson = await response.Content.ReadAsStringAsync();
            using var result = JsonDocument.Parse(responseJson);
            Assert.Equal(17, result.RootElement.GetProperty("id").GetInt32());
            var toolResult = result.RootElement.GetProperty("result");
            var content = toolResult.GetProperty("content");
            if (hasImages)
            {
                Assert.Equal(3, content.GetArrayLength());
                var metadata = toolResult.GetProperty("structuredContent");
                Assert.Equal("test-prompt", metadata.GetProperty("prompt_id").GetString());
                var urls = metadata.GetProperty("image_urls");
                Assert.Equal(2, urls.GetArrayLength());
                Assert.Equal(urls[0].GetString(), metadata.GetProperty("image_url").GetString());
                var markdown = string.Join("\n\n", urls.EnumerateArray().Select((url, index) =>
                    $"![Generated image {index + 1}]({url.GetString()})"));
                Assert.Contains(markdown, content[2].GetProperty("text").GetString());
                Assert.Contains("Display the generated image in your message body", content[2].GetProperty("text").GetString());
                Assert.Equal(markdown, metadata.GetProperty("markdown").GetString());
                foreach (var url in urls.EnumerateArray())
                {
                    Assert.StartsWith(backend.Url + "/view?", url.GetString());
                    Assert.Contains(url.GetString()!, content[2].GetProperty("text").GetString());
                    Assert.Equal(Backend.Png, await http.GetByteArrayAsync(url.GetString()));
                }
                foreach (var image in content.EnumerateArray().Take(2))
                {
                    Assert.Equal("image", image.GetProperty("type").GetString());
                    Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
                    Assert.Equal(Backend.Png, Convert.FromBase64String(image.GetProperty("data").GetString()!));
                }
                Assert.False(toolResult.TryGetProperty("isError", out var error) && error.GetBoolean());
            }
            else
            {
                Assert.True(toolResult.GetProperty("isError").GetBoolean());
                Assert.Contains("no images", content[0].GetProperty("text").GetString());
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            var logs = await output;
            await errors;
            File.Delete(template);
            if (responseJson != null)
            {
                Assert.Contains("MCP response: " + responseJson, logs);
                if (hasImages)
                {
                    var url = backend.Url + "/view?filename=image%20%26%20test.png&subfolder=folder%20%2B%20test&type=output";
                    Assert.Contains("Generated image URL: " + url, logs);
                    Assert.Contains(url, responseJson);
                    Assert.Contains(Convert.ToBase64String(Backend.Png), logs);
                }
            }
        }
    }

    [Fact]
    public async Task MissingCompletionEvent_RetrievesImageFromHistory()
    {
        await using var backend = await Backend.StartAsync();
        backend.History = () => backend.HistoryCalls >= 2 ? CompletedHistory() : "{}";
        using var client = backend.CreateClient();

        Assert.True(await client.WaitForCompletionAsync("test-prompt"));
        var images = await client.GetImagesAsync("test-prompt");

        Assert.Equal(Backend.Png, Assert.Single(images));
        Assert.True(backend.HistoryCalls >= 2);
    }

    [Theory]
    [InlineData("execution_success")]
    [InlineData("executing")]
    public async Task FragmentedCompletionMessage_Completes(string eventType)
    {
        await using var backend = await Backend.StartAsync();
        backend.Socket = async (socket, token) =>
        {
            var message = JsonSerializer.Serialize(new
            {
                type = eventType,
                data = new { prompt_id = "test-prompt", node = (string?)null, padding = new string('x', 6000) }
            });
            var bytes = Encoding.UTF8.GetBytes(message);
            await socket.SendAsync(new ArraySegment<byte>(bytes, 0, 3000), WebSocketMessageType.Text, false, token);
            await socket.SendAsync(new ArraySegment<byte>(bytes, 3000, bytes.Length - 3000), WebSocketMessageType.Text, true, token);
            await Backend.WaitForDisconnect(socket, token);
        };
        using var client = backend.CreateClient();

        Assert.True(await client.WaitForCompletionAsync("test-prompt"));
    }

    [Fact]
    public async Task NodeEventsAndOtherPromptErrors_DoNotFinishGeneration()
    {
        await using var backend = await Backend.StartAsync();
        var eventsSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Socket = async (socket, token) =>
        {
            await Send(socket, "executed", "test-prompt", token);
            await Send(socket, "execution_cached", "test-prompt", token);
            await Send(socket, "execution_error", "another-prompt", token);
            eventsSent.SetResult();
            await finish.Task.WaitAsync(token);
            await Send(socket, "execution_success", "test-prompt", token);
            await Backend.WaitForDisconnect(socket, token);
        };
        using var client = backend.CreateClient();
        var completion = client.WaitForCompletionAsync("test-prompt");
        await eventsSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotSame(completion, await Task.WhenAny(completion, Task.Delay(150)));
        finish.SetResult();
        Assert.True(await completion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClosedOrUnavailableSocket_UsesHistory(bool connect)
    {
        await using var backend = await Backend.StartAsync();
        backend.AcceptSocket = connect;
        backend.Socket = (socket, token) => socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", token);
        backend.History = () => backend.HistoryCalls >= 2 ? CompletedHistory() : "{}";
        using var client = backend.CreateClient();

        Assert.True(await client.WaitForCompletionAsync("test-prompt"));
    }

    [Fact]
    public async Task SilentSocket_RespectsTimeout()
    {
        await using var backend = await Backend.StartAsync();
        using var client = backend.CreateClient(timeout: 1);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.WaitForCompletionAsync("test-prompt").WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SlowSocketConnection_DoesNotDelayCompletedImage()
    {
        await using var backend = await Backend.StartAsync();
        backend.ConnectDelayMs = 10000;
        backend.History = CompletedHistory;
        using var client = backend.CreateClient();

        Assert.True(await client.WaitForCompletionAsync("test-prompt").WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(Backend.Png, Assert.Single(await client.GetImagesAsync("test-prompt")));
    }

    [Fact]
    public async Task CallerCancellation_IsPreserved()
    {
        await using var backend = await Backend.StartAsync();
        using var client = backend.CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.WaitForCompletionAsync("test-prompt", cancellation.Token));
    }

    [Fact]
    public async Task HistoryFailure_ReportsError()
    {
        await using var backend = await Backend.StartAsync();
        backend.History = () => """{"test-prompt":{"status":{"completed":false,"status_str":"error","messages":[["execution_error",{"exception_message":"Model load failed"}]]}}}""";
        using var client = backend.CreateClient();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.WaitForCompletionAsync("test-prompt"));
        Assert.Contains("Model load failed", error.Message);
    }

    [Theory]
    [InlineData("execution_error")]
    [InlineData("execution_interrupted")]
    public async Task MatchingSocketFailure_ReportsError(string eventType)
    {
        await using var backend = await Backend.StartAsync();
        backend.Socket = async (socket, token) =>
        {
            await Send(socket, eventType, "test-prompt", token);
            await Backend.WaitForDisconnect(socket, token);
        };
        using var client = backend.CreateClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.WaitForCompletionAsync("test-prompt"));
    }

    private static Task Send(WebSocket socket, string type, string promptId, CancellationToken token) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            type, data = new { prompt_id = promptId }
        }))), WebSocketMessageType.Text, true, token);

    private static string CompletedHistory() => """
        {"test-prompt":{"status":{"completed":true,"status_str":"success"},"outputs":{"9":{"images":[{"filename":"image & test.png","subfolder":"folder + test","type":"output"}]}}}}
        """;

    private sealed class Backend : IAsyncDisposable
    {
        public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a8x8AAAAASUVORK5CYII=");
        private readonly WebApplication _app;
        public string Url { get; private set; } = "";
        public bool AcceptSocket { get; set; } = true;
        public int ConnectDelayMs { get; set; }
        public Func<string> History { get; set; } = () => "{}";
        public Func<WebSocket, CancellationToken, Task> Socket { get; set; } = WaitForDisconnect;
        public int HistoryCalls { get; private set; }

        private Backend(WebApplication app) => _app = app;

        public static async Task<Backend> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var backend = new Backend(builder.Build());
            backend._app.UseWebSockets();
            backend._app.Map("/ws", async context =>
            {
                if (backend.ConnectDelayMs > 0)
                {
                    await Task.Delay(backend.ConnectDelayMs, context.RequestAborted);
                }
                if (!backend.AcceptSocket)
                {
                    context.Response.StatusCode = 400;
                    return;
                }
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                try { await backend.Socket(socket, context.RequestAborted); }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
            });
            backend._app.MapGet("/history/{promptId}", async context =>
            {
                backend.HistoryCalls++;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(backend.History());
            });
            backend._app.MapPost("/prompt", () => Results.Json(new { prompt_id = "test-prompt" }));
            backend._app.MapGet("/view", (HttpContext context) =>
            {
                Assert.Equal("image & test.png", context.Request.Query["filename"].ToString());
                Assert.Equal("folder + test", context.Request.Query["subfolder"].ToString());
                return Results.Bytes(Png, "image/png");
            });
            await backend._app.StartAsync();
            backend.Url = backend._app.Urls.Single();
            return backend;
        }

        public ComfyUIClient CreateClient(int timeout = 5) => new(new ComfyUISettings
        {
            ApiEndpoint = Url, TimeoutSeconds = timeout, PollIntervalSeconds = 1
        }, NullLogger<ComfyUIClient>.Instance);

        public static async Task WaitForDisconnect(WebSocket socket, CancellationToken token)
        {
            await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), token);
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
