using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Perelegans.Models;

namespace Perelegans.Services;

public sealed class FocusClassificationClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;

    public FocusClassificationClient(HttpClient httpClient, SettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settingsService.Settings.AiApiBaseUrl) &&
        !string.IsNullOrWhiteSpace(_settingsService.Settings.AiApiKey) &&
        !string.IsNullOrWhiteSpace(_settingsService.Settings.AiModel);

    public async Task<FocusAssessmentResult?> ClassifyAsync(
        string processName,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured ||
            !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var prompt = BuildProductivityPrompt(processName, duration);
        var content = await SendOpenAiCompatiblePromptAsync(baseUri, prompt, cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return TryDeserializeJson<FocusAssessmentResult>(content);
    }

    public async Task<ScreenContextAssessmentResult?> AssessScreenAsync(
        string imageBase64,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured ||
            string.IsNullOrWhiteSpace(imageBase64) ||
            !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var requestUri = BuildChatCompletionsUri(baseUri);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settingsService.Settings.AiApiKey.Trim());

        var payload = new
        {
            model = _settingsService.Settings.AiModel.Trim(),
            temperature = 0.2,
            max_tokens = 300,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You are a strict focus-state classifier. Return JSON only."
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "text",
                            text = "判断这张桌面截图是否体现深度学习、编程、写作、阅读论文或其他工作状态。若是娱乐视频、社交闲逛、游戏或无关浏览，判为 false。返回 JSON：{\"isDeepWork\":true,\"reason\":\"...\",\"message\":\"...\",\"confidence\":0.0}。"
                        },
                        new
                        {
                            type = "image_url",
                            image_url = new { url = $"data:image/jpeg;base64,{imageBase64}" }
                        }
                    }
                }
            }
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        var content = ExtractOpenAiContent(body);
        return string.IsNullOrWhiteSpace(content)
            ? null
            : TryDeserializeJson<ScreenContextAssessmentResult>(content);
    }

    public static string BuildProductivityPrompt(string processName, TimeSpan duration)
    {
        var minutes = Math.Max(1, (int)Math.Round(duration.TotalMinutes));
        return
            "你是高校答辩演示中的桌面专注力管家。请进行零样本分类。\n" +
            $"检测到的前台进程：{processName}\n" +
            $"持续驻留时间：{minutes} 分钟\n" +
            "判断该应用在当前语境下是否通常属于生产力/学习工具，并给出一句悬浮气泡提示。\n" +
            "必须只返回 JSON，不要 Markdown，不要解释。格式：\n" +
            "{\"isProductive\":true,\"description\":\"一句话描述该应用可能用途\",\"message\":\"给用户的一句鼓励或警告\",\"confidence\":0.0}";
    }

    private async Task<string?> SendOpenAiCompatiblePromptAsync(
        Uri baseUri,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildChatCompletionsUri(baseUri);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settingsService.Settings.AiApiKey.Trim());

        var payload = new
        {
            model = _settingsService.Settings.AiModel.Trim(),
            temperature = 0.2,
            max_tokens = 300,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You classify desktop app usage for a focus assistant. Return compact JSON only."
                },
                new
                {
                    role = "user",
                    content = userPrompt
                }
            }
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        return ExtractOpenAiContent(body);
    }

    private static string? ExtractOpenAiContent(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var first = choices.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined ||
            !first.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        return content.GetString();
    }

    private static Uri BuildChatCompletionsUri(Uri baseUri)
    {
        if (baseUri.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return baseUri;
        }

        var baseText = baseUri.ToString().TrimEnd('/');
        return new Uri($"{baseText}/chat/completions");
    }

    private static T? TryDeserializeJson<T>(string text) where T : class
    {
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start
            ? text[start..(end + 1)]
            : null;
    }
}
