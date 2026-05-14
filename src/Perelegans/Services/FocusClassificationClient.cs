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

    public async Task<MemoryCandidateResult?> ExtractMemoryCandidateAsync(
        string userInput,
        string contextPack,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured ||
            string.IsNullOrWhiteSpace(userInput) ||
            !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var prompt =
            "You are the memory layer of a local-first personal desktop assistant.\n" +
            "Decide whether the user's message contains a durable memory worth saving locally.\n" +
            "Only save stable preferences, project context, decisions, workflows, application/tool facts, or concise notes.\n" +
            "Do not save passwords, secrets, one-off casual chat, or sensitive personal data.\n" +
            "If the new information conflicts with existing memories, summarize the newer statement without deleting anything.\n\n" +
            "Existing local context:\n" + contextPack + "\n\n" +
            "User message:\n" + userInput.Trim() + "\n\n" +
            "Return JSON only. Schema:\n" +
            "{\"shouldRemember\":true,\"type\":\"Preference|Project|Decision|Workflow|Application|Note\",\"title\":\"short title\",\"content\":\"one durable memory sentence\",\"tags\":[\"tag\"],\"weight\":0.0,\"reply\":\"short user-facing note\",\"confidence\":0.0}";

        var content = await SendOpenAiCompatiblePromptAsync(baseUri, prompt, cancellationToken, maxTokens: 420);
        return string.IsNullOrWhiteSpace(content)
            ? null
            : TryDeserializeJson<MemoryCandidateResult>(content);
    }

    public async Task<PersonalizedReplyResult?> CreatePersonalizedReplyAsync(
        string userInput,
        string contextPack,
        ForegroundFocusSnapshot? foregroundSnapshot,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured ||
            string.IsNullOrWhiteSpace(userInput) ||
            !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var foreground = foregroundSnapshot == null
            ? "No foreground application snapshot is available."
            : $"Foreground app: {foregroundSnapshot.ProcessName}; duration: {Math.Max(1, (int)foregroundSnapshot.Duration.TotalMinutes)} minutes; path: {foregroundSnapshot.ExecutablePath}";

        var prompt =
            "You are Perelegans, a lightweight local-first Windows assistant.\n" +
            "You help by using explicit local memories and current desktop context. You are not a focus-mode coach; do not judge the user or push discipline.\n" +
            "Answer naturally in the user's language. Be concise, concrete, and transparent when using local memory.\n\n" +
            "Relevant local memories:\n" + contextPack + "\n\n" +
            "Current desktop context:\n" + foreground + "\n\n" +
            "User message:\n" + userInput.Trim() + "\n\n" +
            "Return JSON only. Schema:\n" +
            "{\"reply\":\"assistant reply\",\"usedMemoryIds\":[1,2],\"suggestedMemory\":{\"shouldRemember\":false,\"type\":\"Note\",\"title\":\"\",\"content\":\"\",\"tags\":[],\"weight\":0.0,\"reply\":\"\",\"confidence\":0.0}}";

        var content = await SendOpenAiCompatiblePromptAsync(baseUri, prompt, cancellationToken, maxTokens: 700);
        return string.IsNullOrWhiteSpace(content)
            ? null
            : TryDeserializeJson<PersonalizedReplyResult>(content);
    }

    public async Task<TaskInstructionResult?> ParseTaskInstructionAsync(
        string userInput,
        string? currentFocusGoal,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured ||
            string.IsNullOrWhiteSpace(userInput) ||
            !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var prompt = BuildTaskInstructionPrompt(userInput, currentFocusGoal);
        var content = await SendOpenAiCompatiblePromptAsync(baseUri, prompt, cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return TryDeserializeJson<TaskInstructionResult>(content);
    }

    public async Task<TaskAdventureDraft?> CreateTaskAdventureAsync(
        string taskTitle,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured ||
            string.IsNullOrWhiteSpace(taskTitle) ||
            !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var prompt =
            "你是一个个人行动系统的 AI 任务分析师。请把用户输入的任务转成可执行的任务洞察，不要使用 RPG、游戏、冒险或战斗叙事。\n" +
            "目标：帮助用户知道这件事是什么、下一步怎么开始、它属于哪个长期行动星座。\n" +
            "questTitle 是 12 字以内的短标题；questNarrative 是一句自然、实用的任务说明；rewardName 是完成后的真实产出名称。\n" +
            "summary 是任务目的摘要；nextAction 必须是 15 分钟内可启动的具体动作；difficulty 为 1-5；estimatedMinutes 为合理分钟数。\n" +
            "tags 给 2-6 个语义标签；constellationName 给一个稳定的星座名，用于把相似任务聚类，例如“写作星座”“WPF开发星座”“考试复习星座”。\n" +
            $"用户任务：{taskTitle.Trim()}\n" +
            "必须只返回 JSON，不要 Markdown，不要解释。格式：\n" +
            "{\"questTitle\":\"整理论文摘要\",\"questNarrative\":\"把论文摘要整理成可提交的清晰版本。\",\"rewardName\":\"论文摘要初稿\",\"summary\":\"完成论文摘要的结构梳理和语言修订。\",\"nextAction\":\"打开文档，先列出摘要的三段结构。\",\"difficulty\":3,\"estimatedMinutes\":45,\"tags\":[\"写作\",\"论文\",\"整理\"],\"constellationName\":\"论文写作星座\"}";

        var content = await SendOpenAiCompatiblePromptAsync(baseUri, prompt, cancellationToken);
        return string.IsNullOrWhiteSpace(content)
            ? null
            : TryDeserializeJson<TaskAdventureDraft>(content);
    }

    public async Task<TaskCompletionDraft?> CreateTaskCompletionAsync(
        string taskTitle,
        string? rewardName,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured ||
            string.IsNullOrWhiteSpace(taskTitle) ||
            !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var reward = string.IsNullOrWhiteSpace(rewardName) ? "完成产出" : rewardName.Trim();
        var prompt =
            "你是一个个人行动系统的完成复盘助手。\n" +
            "用户刚完成一个现实任务，请生成一句简短、具体、不夸张的完成总结，并指出实际产出。\n" +
            $"现实任务：{taskTitle.Trim()}\n" +
            $"候选产出：{reward}\n" +
            "必须只返回 JSON，不要 Markdown，不要解释。格式：\n" +
            "{\"completionNarrative\":\"已完成论文摘要整理，得到一版结构清晰、可继续润色的初稿。\",\"rewardName\":\"论文摘要初稿\"}";

        var content = await SendOpenAiCompatiblePromptAsync(baseUri, prompt, cancellationToken);
        return string.IsNullOrWhiteSpace(content)
            ? null
            : TryDeserializeJson<TaskCompletionDraft>(content);
    }

    public async Task<DailyReviewDraft?> CreateDailyReviewAsync(
        IReadOnlyCollection<FocusTask> tasks,
        IReadOnlyCollection<ApplicationUsage> applications,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured ||
            !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var today = DateTime.Today;
        var taskLines = tasks
            .Where(task => task.CreatedAt.Date == today || task.CompletedAt?.Date == today)
            .OrderBy(task => task.CreatedAt)
            .Take(20)
            .Select(task =>
                $"- {task.Title} | 状态:{task.Status} | 星座:{task.ConstellationName} | 标签:{task.Tags} | 下一步:{task.NextAction}")
            .ToList();
        if (taskLines.Count == 0)
        {
            taskLines.Add("- 今天还没有任务星点。");
        }

        var appLines = applications
            .OrderByDescending(app => app.TotalDuration)
            .Take(8)
            .Select(app => $"- {app.DisplayName} | {Math.Max(1, (int)Math.Round(app.TotalDuration.TotalMinutes))}分钟 | {app.Category}")
            .ToList();
        if (appLines.Count == 0)
        {
            appLines.Add("- 暂无应用使用记录。");
        }

        var prompt =
            "你是一个克制、具体的个人行动复盘助手。请根据今日任务星点和应用使用记录生成简短日报。\n" +
            "不要使用 RPG 或游戏叙事。不要说教。重点指出：真正推进了什么、潜在风险、明天/下一步最小动作。\n\n" +
            $"当前专注目标：{NormalizeFocusGoal(_settingsService.Settings.FocusGoal)}\n" +
            "今日任务：\n" + string.Join('\n', taskLines) + "\n\n" +
            "主要应用：\n" + string.Join('\n', appLines) + "\n\n" +
            "必须只返回 JSON，不要 Markdown，不要解释。格式：\n" +
            "{\"review\":\"今天主要推进了论文摘要和代码验证，专注信号集中在写作与开发。\",\"highlights\":[\"完成论文摘要初稿\",\"WPF 星图交互推进明显\"],\"risks\":[\"任务颗粒度仍偏大\"],\"suggestedNextAction\":\"明天先用 15 分钟把星图删除流程手动测一遍。\"}";

        var content = await SendOpenAiCompatiblePromptAsync(baseUri, prompt, cancellationToken, maxTokens: 520);
        return string.IsNullOrWhiteSpace(content)
            ? null
            : TryDeserializeJson<DailyReviewDraft>(content);
    }

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

        var prompt = BuildProductivityPrompt(
            processName,
            duration,
            _settingsService.Settings.FocusGoal);
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

        var focusGoal = NormalizeFocusGoal(_settingsService.Settings.FocusGoal);
        var screenPrompt =
            "请判断这张桌面截图是否体现专注、学习、创作、开发、阅读、写作或其他生产力工作状态。\n" +
            $"当前专注目标：{focusGoal}\n" +
            "如果截图内容明显有助于该目标或通用专注工作，isDeepWork 为 true；如果更像娱乐视频、社交闲逛、游戏、无目的浏览或明显分心，isDeepWork 为 false。\n" +
            "只返回 JSON，不要 Markdown，不要解释。格式：{\"isDeepWork\":true,\"reason\":\"...\",\"message\":\"...\",\"confidence\":0.0}";

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
                    content = "You are a strict desktop focus-state classifier. Return JSON only."
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "text",
                            text = screenPrompt
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

    public static string BuildProductivityPrompt(string processName, TimeSpan duration, string? focusGoal)
    {
        var minutes = Math.Max(1, (int)Math.Round(duration.TotalMinutes));
        var normalizedGoal = NormalizeFocusGoal(focusGoal);
        return
            "你是一个桌面专注状态分类器。请根据前台应用名称和停留时长进行零样本分类。\n" +
            $"当前专注目标：{normalizedGoal}\n" +
            $"检测到的前台进程：{processName}\n" +
            $"持续停留时间：{minutes} 分钟\n" +
            "判断该应用在当前目标下是否通常有助于专注、学习、创作、开发、阅读、写作或其他生产力工作。\n" +
            "如果该应用更偏娱乐、社交、无目的浏览、游戏或明显分心，isProductive 为 false。\n" +
            "不要预设用户正在执行任何没有在专注目标中声明的具体任务。\n" +
            "请给出一句适合悬浮气泡展示的中文提示。\n" +
            "必须只返回 JSON，不要 Markdown，不要解释。格式：\n" +
            "{\"isProductive\":true,\"description\":\"一句话描述该应用可能用途\",\"message\":\"给用户的一句鼓励或提醒\",\"confidence\":0.0}";
    }

    private static string BuildTaskInstructionPrompt(string userInput, string? currentFocusGoal)
    {
        var currentGoal = string.IsNullOrWhiteSpace(currentFocusGoal)
            ? "无"
            : currentFocusGoal.Trim();

        return
            "你是桌面专注助手的自然语言输入解析器。你的任务是从用户输入中识别：\n" +
            "1. 是否包含要完成的真实任务；\n" +
            "2. 是否是应用调度命令；\n" +
            "3. 如果包含多个任务，请拆成独立、可执行的短任务。\n\n" +
            "不要把闲聊、疑问、感谢、抱怨、状态询问、无明确行动目标的话当作任务。\n" +
            "任务必须是用户准备完成的具体事情，例如复习高数、写论文摘要、实现登录页、整理会议纪要。\n" +
            "调度命令只允许这些值：start_monitor, pause_monitor, complete_task, daily_review, refresh, settings, backup, restore, clear_data, none。\n" +
            "intent 只允许这些值：task, command, mixed, none。\n" +
            "如果既有命令又有任务，intent 为 mixed。\n" +
            "如果用户要求切换任务或设置任务，将任务放入 tasks。\n" +
            "如果没有明确任务且没有命令，intent 为 none，tasks 为空。\n" +
            "assistantMessage 用当前用户语言简短回复，不超过 20 个汉字或 12 个英文词。\n\n" +
            $"当前专注任务：{currentGoal}\n" +
            $"用户输入：{userInput.Trim()}\n\n" +
            "必须只返回 JSON，不要 Markdown，不要解释。格式：\n" +
            "{\"intent\":\"task\",\"command\":\"none\",\"primaryTask\":\"复习高数错题\",\"tasks\":[\"复习高数错题\"],\"assistantMessage\":\"已识别任务\",\"confidence\":0.9}";
    }

    private async Task<string?> SendOpenAiCompatiblePromptAsync(
        Uri baseUri,
        string userPrompt,
        CancellationToken cancellationToken,
        int maxTokens = 300)
    {
        var requestUri = BuildChatCompletionsUri(baseUri);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settingsService.Settings.AiApiKey.Trim());

        var payload = new
        {
            model = _settingsService.Settings.AiModel.Trim(),
            temperature = 0.2,
            max_tokens = maxTokens,
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

    private static string NormalizeFocusGoal(string? focusGoal)
    {
        return string.IsNullOrWhiteSpace(focusGoal)
            ? "通用专注：学习、创作、开发、阅读、写作或生产力工作"
            : focusGoal.Trim();
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
