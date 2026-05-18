using System.Text.Json.Serialization;

namespace Perelegans.Models;

public enum AppCloseBehavior
{
    Exit = 0,
    MinimizeToTray = 1
}

public enum AiProvider
{
    Auto = 0,
    OpenAI = 1,
    OpenRouter = 2,
    Anthropic = 3
}

/// <summary>
/// Application settings persisted as JSON.
/// </summary>
public class AppSettings
{
    public static readonly string DefaultAiPersonalityPrompt = string.Join(Environment.NewLine, new[]
    {
        "你是 Perelegans，一个专属于用户的本地陪伴式 AI 助手。你像熟悉的长期搭档，会陪用户积累上下文、整理记忆、理解工作节奏，也会在用户卡住时给一点可靠、温和的推动。",
        "",
        "身份与目标：",
        "- 你会尽可能记住关于用户的稳定事实、计划、偏好、项目、工作流和阶段性状态，并在对话中帮助用户把这些记忆重新连接起来。",
        "- 你重视本地优先和隐私边界。你只能基于已经提供的本地记忆、当前对话和桌面上下文做判断；证据不足时要明说，不要假装知道。",
        "- 你可以主动把本地记忆、计划状态和进程上下文结合起来，帮用户恢复现场、发现下一步、减少重复解释。",
        "",
        "语气：",
        "- 语言要亲和、轻松、有人味，可以多给一点确认和鼓励，让用户感觉自己有能力继续推进。",
        "- 鼓励要具体：肯定用户已经做出的选择、留下的线索、完成的小步骤，而不是泛泛地说“你很棒”。",
        "- 不要说教，不要制造压力，不要替用户强行安排人生。你可以提醒、建议、陪着梳理，但语气要像站在用户这边。",
        "- 可以自然地称呼自己为“我”或“Perelegans”，让对话像长期搭档之间的交流。",
        "- 避免固定口头禅和高频套话，不要反复使用同一类表达。",
        "",
        "记忆 callback：",
        "- 当本地记忆与当前话题相关时，要自然 callback 一条或少量关键记忆，例如用户正在推进的 plan、常用工具、偏好、最近项目或已完成状态。",
        "- callback 要短、准、有用，不要为了展示记忆而生硬插入。",
        "- 如果记忆可能过期、冲突或证据不足，要用“我这里看到的是……”这类表达保留不确定性。",
        "",
        "行动方式：",
        "- 优先给出能立刻继续推进的下一步，而不是长篇解释。",
        "- 对 plan 记忆要关注是否完成；未完成的 plan 可以作为未来意图，已完成的 plan 可以作为产出证据。",
        "- 当用户表达“我要、我计划、我准备”等未来意图时，倾向于帮助它沉淀成清晰的 plan 记忆。",
        "- 当用户只是在聊天、抱怨或犹豫时，先回应情绪，再给一个很小、很容易开始的选项。",
        "",
        "边界：",
        "- 不要编造本地没有的记忆、文件内容或桌面行为。",
        "- 不要保存密码、密钥、一次性闲聊或敏感隐私作为记忆。",
        "- 如果结构化 JSON 输出、工具约束或可靠性要求与人格语气冲突，永远优先遵守结构化输出和事实可靠性。"
    });

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>
    /// Process monitoring interval in seconds.
    /// </summary>
    public int MonitorIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Whether process monitoring is enabled on startup.
    /// </summary>
    public bool MonitorEnabled { get; set; } = true;

    /// <summary>
    /// Whether coding client activity can drive the floating pet state.
    /// </summary>
    public bool CodingClientMonitorEnabled { get; set; } = true;

    /// <summary>
    /// Whether Codex Desktop activity should be monitored.
    /// </summary>
    public bool CodexDesktopMonitorEnabled { get; set; } = true;

    /// <summary>
    /// Whether Claude Desktop activity should be monitored.
    /// </summary>
    public bool ClaudeDesktopMonitorEnabled { get; set; } = true;

    /// <summary>
    /// Selected floating pet skin preset.
    /// </summary>
    public string FloatingPetSkinId { get; set; } = PetSkinPresets.Pink;

    /// <summary>
    /// UI Language code (e.g. zh-Hans, en-US).
    /// </summary>
    public string Language { get; set; } = "zh-Hans";

    /// <summary>
    /// Whether the app should register itself to launch at Windows sign-in.
    /// </summary>
    public bool LaunchAtStartup { get; set; }

    /// <summary>
    /// Behavior to apply when the main window is closed.
    /// </summary>
    public AppCloseBehavior CloseBehavior { get; set; } = AppCloseBehavior.Exit;

    /// <summary>
    /// AI provider protocol.
    /// </summary>
    public AiProvider AiProvider { get; set; } = AiProvider.Auto;

    /// <summary>
    /// AI API base URL (e.g. https://api.openai.com/v1).
    /// </summary>
    public string AiApiBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// AI API key.
    /// </summary>
    public string AiApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Model identifier for local-context assistant features.
    /// </summary>
    public string AiModel { get; set; } = string.Empty;

    /// <summary>
    /// User-editable assistant personality prompt for conversational replies.
    /// </summary>
    public string AiPersonalityPrompt { get; set; } = DefaultAiPersonalityPrompt;

    /// <summary>
    /// Optional user-defined context goal retained for compatibility with older settings.
    /// </summary>
    public string FocusGoal { get; set; } = string.Empty;

    /// <summary>
    /// Whether the assistant may save high-confidence local memories automatically.
    /// </summary>
    public bool AutoSaveMemories { get; set; } = true;
}
