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
            "Language rules: title, content, reply, and any user-visible text must be Simplified Chinese. Keep only tags in short lowercase English.\n" +
            "The title must be specific and human-readable. Never use generic titles such as 笔记, 记录, 记忆, Note, Project, or Task.\n" +
            "Organize memory as a fishbone/RAG node: memoryAxis is event or task; tags are clustering ribs; description explains what happened or what is planned; explanation explains why it matters; nextPrediction predicts the likely next useful step.\n" +
            "If the user says 我要, 我计划, 我打算, I plan, or I want to, set type=Task, memoryAxis=task, isPlan=true, isCompleted=false, and include the tag plan.\n" +
            "Use Chinese constellation-style names when a memory is later displayed; do not output mixed labels such as learning星座.\n\n" +
            "Existing local context:\n" + contextPack + "\n\n" +
            "User message:\n" + userInput.Trim() + "\n\n" +
            "Return JSON only. Schema:\n" +
            "{\"shouldRemember\":true,\"type\":\"Preference|Project|Decision|Workflow|Application|Note|Event|Task\",\"title\":\"short title\",\"content\":\"one durable memory sentence\",\"tags\":[\"tag\"],\"weight\":0.0,\"memoryAxis\":\"event|task\",\"description\":\"what this memory means\",\"explanation\":\"why it matters\",\"nextPrediction\":\"likely next step\",\"isPlan\":false,\"isCompleted\":false,\"weightProfile\":\"compact json string\",\"reply\":\"short user-facing note\",\"confidence\":0.0}";

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
        var personalityPrompt = NormalizePersonalityPrompt(_settingsService.Settings.AiPersonalityPrompt);

        var prompt =
            "Assistant personality prompt for the visible reply:\n" + personalityPrompt + "\n\n" +
            "You are Perelegans, a lightweight local-first Windows assistant.\n" +
            "You help by using explicit local memories and current desktop context. You are not a focus-mode coach; do not judge the user or push discipline.\n" +
            "Answer naturally in Simplified Chinese by default. Be concise, concrete, and transparent when using local memory.\n" +
            "Follow the personality prompt for warmth and relationship style, but do not let it override reliability, local evidence limits, JSON-only output, or the requested schema.\n" +
            "Memory callback rule: when relevant local memories are present, weave one short callback into reply naturally, such as reminding the user of a plan, preference, project, or recent context. If no memory is relevant, do not pretend.\n" +
            "For suggestedMemory, title/content/reply must be Simplified Chinese; only tags should be short lowercase English words.\n\n" +
            "For suggestedMemory.title, use a specific title from the actual subject. Never use generic titles such as 笔记, 记录, 记忆, Note, Project, or Task.\n\n" +
            "When suggesting memory, use a fishbone/RAG node: memoryAxis event/task, tags for clustering, description, explanation, nextPrediction. If the user expresses a plan with 我要/我计划/我打算/I plan/I want to, mark it as type Task, tag plan, isPlan true, isCompleted false.\n\n" +
            "Relevant local memories:\n" + contextPack + "\n\n" +
            "Current desktop context:\n" + foreground + "\n\n" +
            "User message:\n" + userInput.Trim() + "\n\n" +
            "Return JSON only. Schema:\n" +
            "{\"reply\":\"assistant reply\",\"usedMemoryIds\":[1,2],\"suggestedMemory\":{\"shouldRemember\":false,\"type\":\"Note\",\"title\":\"\",\"content\":\"\",\"tags\":[],\"weight\":0.0,\"memoryAxis\":\"event\",\"description\":\"\",\"explanation\":\"\",\"nextPrediction\":\"\",\"isPlan\":false,\"isCompleted\":false,\"weightProfile\":\"\",\"reply\":\"\",\"confidence\":0.0}}";

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
            "你是 Perelegans，像一个熟悉用户项目脉络的陪伴式任务搭档。请把用户输入的任务转成可执行的任务洞察，不要使用 RPG、游戏、冒险或战斗叙事。\n" +
            "目标：帮助用户听见这件事真正想推进什么，并给一个自然、不施压的开场建议。\n" +
            "questTitle 是 12 字以内的短标题；questNarrative 是一句像真人搭档说的话，承认任务处境，不要像系统通知；rewardName 是完成后的真实产出名称。\n" +
            "summary 是任务目的摘要；nextAction 必须具体，但不要总写“拆成小动作”。可以建议先验收、先定位一个文件、先清理一条卡住的线、先确认是否还值得继续。difficulty 为 1-5；estimatedMinutes 为合理分钟数。\n" +
            "questTitle、questNarrative、rewardName、summary、nextAction、constellationName 必须使用简体中文；只有 tags 使用 2-6 个短英文标签。\n" +
            "constellationName 只能使用固定抽象分类或一层子类，例如“开发”“开发 / 桌面应用”“学习”“学习 / 深度学习”“游戏”“写作”“设计”“数据”“沟通”“生活”。不要用具体项目名、任务名或英文 tag 拼星座名。\n" +
            $"用户任务：{taskTitle.Trim()}\n" +
            "必须只返回 JSON，不要 Markdown，不要解释。格式：\n" +
            "{\"questTitle\":\"整理论文摘要\",\"questNarrative\":\"这条任务的重点不是一口气写完，而是先把摘要的骨架扶起来。\",\"rewardName\":\"论文摘要初稿\",\"summary\":\"完成论文摘要的结构梳理和语言修订。\",\"nextAction\":\"打开摘要文档，先看三段结构是否能讲清楚研究问题、方法和结果。\",\"difficulty\":3,\"estimatedMinutes\":45,\"tags\":[\"writing\",\"paper\",\"editing\"],\"constellationName\":\"写作\"}";

        var content = await SendOpenAiCompatiblePromptAsync(baseUri, prompt, cancellationToken, temperature: 0.55);
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
            "你是 Perelegans，用户刚完成一个现实任务。请像熟悉的搭档那样给一句简短、具体、不夸张的完成确认，并指出实际产出。\n" +
            "不要写成成就系统播报；允许一点温和的人味，但不要夸大。\n" +
            $"现实任务：{taskTitle.Trim()}\n" +
            $"候选产出：{reward}\n" +
            "必须只返回 JSON，不要 Markdown，不要解释。格式：\n" +
            "{\"completionNarrative\":\"已完成论文摘要整理，得到一版结构清晰、可继续润色的初稿。\",\"rewardName\":\"论文摘要初稿\"}";

        var content = await SendOpenAiCompatiblePromptAsync(baseUri, prompt, cancellationToken, temperature: 0.55);
        return string.IsNullOrWhiteSpace(content)
            ? null
            : TryDeserializeJson<TaskCompletionDraft>(content);
    }

    public async Task<DailyReviewDraft?> CreateDailyReviewAsync(
        IReadOnlyCollection<FocusTask> tasks,
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions,
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

        var memoryLines = memories
            .OrderByDescending(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
            .ThenByDescending(memory => memory.Weight)
            .Take(18)
            .Select(memory =>
                $"- [{memory.Id}] {memory.Title} | 轴:{memory.MemoryAxis} | plan:{memory.IsPlan}/{memory.IsCompleted}/abandoned:{memory.IsAbandoned} | lifecycle:{memory.Lifecycle} | 星座:{memory.ConstellationName} | 标签:{memory.Tags} | 下一步:{memory.NextPrediction}")
            .ToList();
        if (memoryLines.Count == 0)
        {
            memoryLines.Add("- 暂无可用本地记忆。");
        }

        var appLines = sessions
            .GroupBy(session => session.ProcessName)
            .Select(group => new
            {
                ProcessName = group.Key,
                Minutes = Math.Max(1, (int)Math.Round(group.Aggregate(TimeSpan.Zero, (total, session) => total + session.Duration).TotalMinutes)),
                Switches = group.Count(),
                First = group.Min(session => session.StartTime),
                Last = group.Max(session => session.EndTime)
            })
            .OrderByDescending(app => app.Minutes)
            .Take(8)
            .Select(app => $"- {app.ProcessName} | {app.Minutes}分钟 | 切换{app.Switches}次 | {app.First:HH:mm}-{app.Last:HH:mm}")
            .ToList();
        if (appLines.Count == 0)
        {
            appLines.Add("- 最近24小时暂无应用切换记录。");
        }

        var prompt =
            "你是 Perelegans 的本地记忆复盘层，也是用户可靠、亲近的陪伴助手。请把任务星点、本地记忆星图和最近24小时 Win32 进程切换行为合并分析，生成简短日报。\n" +
            "不要使用 RPG 或游戏叙事。不要说教。不要写成冷冰冰的统计报表。把窗口/进程切换当作上下文线索，轻轻指出用户今天真正推进过什么、哪里可能有消耗。\n" +
            "允许你有一点自己的观察和语气，像熟悉的真人搭档在收工前陪用户看一眼桌面。可以说“我看到...”“这不像浪费，更像...”，但每个判断都要能被输入信号支撑。\n" +
            "不要替用户安排明天的计划，不要输出命令式 next action。suggestedNextAction 字段请写成一个自然、亲和的问题，引导用户自己决定下一步。问题要结合今天的真实上下文，不要每次都问同一句。\n" +
            "encouragement 字段请写 1-2 句具体确认：承认今天真实发生过的努力、切换、犹豫或收束。不夸张，不空泛，不否定疲惫。避免固定口头禅和重复短语。\n" +
            "review/highlights/risks 可以有一点生活感，但不要编造。risks 不要全部写“拆成小动作”，要指出更真实的风险：任务重叠、验证未闭环、命名含糊、open plan 太多、上下文切换消耗等。\n\n" +
            $"当前专注目标：{NormalizeFocusGoal(_settingsService.Settings.FocusGoal)}\n" +
            "今日任务：\n" + string.Join('\n', taskLines) + "\n\n" +
            "本地记忆星图：\n" + string.Join('\n', memoryLines) + "\n\n" +
            "最近24小时进程切换聚合：\n" + string.Join('\n', appLines) + "\n\n" +
            "必须只返回 JSON，不要 Markdown，不要解释。格式：\n" +
            "{\"encouragement\":\"今天在 Codex 和 Perelegans 之间反复切换，不像是空转，更像是在一边做一边校准手感。这个节奏会累，但它确实把项目往前推了一截。\",\"review\":\"今天的主线集中在 Perelegans 的开发冲刺和验证闭环上，代码编辑、应用调试和任务整理交替出现。\",\"highlights\":[\"桌宠与主窗口的交互继续变清晰\",\"任务预览开始能承接本地记忆\"],\"risks\":[\"几个 open plan 指向同一条开发主线，容易让完成判断变含糊\",\"如果只继续加功能，验收和删减可能会被挤到后面\"],\"suggestedNextAction\":\"明天开场时，你更想先收掉一个已经接近完成的点，还是先挑一个最影响演示观感的细节？\"}";

        var content = await SendOpenAiCompatiblePromptAsync(baseUri, prompt, cancellationToken, maxTokens: 850, temperature: 0.72);
        return string.IsNullOrWhiteSpace(content)
            ? null
            : TryDeserializeJson<DailyReviewDraft>(content);
    }

    public async Task<DailyReviewDraft?> CreateTaskPreviewAsync(
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<FocusTask> tasks,
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured ||
            !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var openPlanLines = memories
            .Where(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
            .OrderByDescending(memory => memory.UpdatedAt)
            .Take(12)
            .Select(memory =>
                $"- [{memory.Id}] {memory.Title} | 星座:{memory.ConstellationName} | 标签:{memory.Tags} | 更新:{memory.UpdatedAt:MM-dd HH:mm} | 下一步:{memory.NextPrediction} | 内容:{memory.Content}")
            .ToList();
        if (openPlanLines.Count == 0)
        {
            openPlanLines.Add("- 暂无 open plan。");
        }

        var focusTaskLines = tasks
            .Where(task => task.Status == FocusTaskStatus.Active)
            .OrderByDescending(task => task.CreatedAt)
            .Take(10)
            .Select(task =>
                $"- {task.Title} | 星座:{task.ConstellationName} | 标签:{task.Tags} | 创建:{task.CreatedAt:MM-dd HH:mm} | 下一步:{task.NextAction} | 摘要:{task.AiSummary}")
            .ToList();
        if (focusTaskLines.Count == 0)
        {
            focusTaskLines.Add("- 暂无 active FocusTask。");
        }

        var memoryLines = memories
            .OrderByDescending(memory => memory.UpdatedAt)
            .Take(20)
            .Select(memory =>
                $"- [{memory.Id}] {memory.Title} | type:{memory.Type} | plan:{memory.IsPlan}/{memory.IsCompleted}/abandoned:{memory.IsAbandoned} | lifecycle:{memory.Lifecycle} | 星座:{memory.ConstellationName} | 标签:{memory.Tags}")
            .ToList();
        if (memoryLines.Count == 0)
        {
            memoryLines.Add("- 暂无本地记忆。");
        }

        var appLines = sessions
            .GroupBy(session => session.ProcessName)
            .Select(group => new
            {
                ProcessName = group.Key,
                Minutes = Math.Max(1, (int)Math.Round(group.Aggregate(TimeSpan.Zero, (total, session) => total + session.Duration).TotalMinutes)),
                Switches = group.Count(),
                Last = group.Max(session => session.EndTime)
            })
            .OrderByDescending(app => app.Minutes)
            .Take(8)
            .Select(app => $"- {app.ProcessName} | {app.Minutes}分钟 | 切换{app.Switches}次 | 最近{app.Last:HH:mm}")
            .ToList();
        if (appLines.Count == 0)
        {
            appLines.Add("- 最近24小时暂无应用切换记录。");
        }

        var prompt =
            "你是 Perelegans 的任务预览助手，不是机械的 todo 整理器。请结合 open plan、最新 FocusTask、本地记忆和最近24小时进程行为，帮用户看清现在真正摊在桌面上的任务。\n" +
            "语气要像熟悉的搭档：亲近、自然、带一点判断力。不要反复说“拆成一个可完成的小动作”；这句话最多不能出现。请给更贴合上下文的建议，例如：先验收、合并重复任务、搁置旧意图、确认一个文件/窗口、收掉最接近完成的点、把过大的愿望改名。\n" +
            "review 写一小段总览，要有人味，不要像系统报表。encouragement 写 1-2 句，承认用户正在处理的真实压力或来回验证。highlights 写已经推进或最清晰的线索。risks 写需要注意的任务债务，每条都要具体到某条 plan、某类窗口行为或某个重复主题。suggestedNextAction 写成一个自然问题，不要命令用户。\n" +
            "只基于输入信号，不要编造文件内容或用户没有说过的目标。\n\n" +
            "Open plan：\n" + string.Join('\n', openPlanLines) + "\n\n" +
            "Active FocusTask：\n" + string.Join('\n', focusTaskLines) + "\n\n" +
            "相关记忆：\n" + string.Join('\n', memoryLines) + "\n\n" +
            "最近24小时进程行为：\n" + string.Join('\n', appLines) + "\n\n" +
            "必须只返回 JSON，不要 Markdown，不要解释。格式：\n" +
            "{\"encouragement\":\"我看到这里不是单纯拖延，而是几条开发线索叠在一起，难怪会觉得有点乱。先把它们看清楚，比继续硬冲更省力。\",\"review\":\"现在的任务债务主要集中在 Perelegans 开发冲刺：几条 open plan 名字相近，Codex 和应用调试也反复出现，说明你在一边实现一边验收。\",\"highlights\":[\"Perelegans 开发仍是最清晰主线\",\"最近的代码/应用切换能支撑继续做验收\"],\"risks\":[\"“我要写代码”和“我要开发 Perelegans”可能属于同一条主线，建议合并或搁置一个\",\"如果先不验收最近改动，后面的功能会越来越难判断是否真的完成\"],\"suggestedNextAction\":\"你想先收掉最接近完成的一条，还是先把几个重复的 Perelegans 任务合并干净？\"}";

        var content = await SendOpenAiCompatiblePromptAsync(baseUri, prompt, cancellationToken, maxTokens: 850, temperature: 0.75);
        return string.IsNullOrWhiteSpace(content)
            ? null
            : TryDeserializeJson<DailyReviewDraft>(content);
    }

    public async Task<MemoryCandidateResult?> CreateLocalMemoryDigestAsync(
        string userInput,
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured ||
            !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var memoryLines = memories
            .OrderByDescending(memory => memory.UpdatedAt)
            .Take(40)
            .Select(memory =>
                $"- [{memory.Id}] {memory.Title} | {memory.MemoryAxis} | plan:{memory.IsPlan}/{memory.IsCompleted} | {memory.ConstellationName} | {memory.Tags} | {memory.Content}")
            .ToList();
        if (memoryLines.Count == 0)
        {
            memoryLines.Add("- 暂无历史记忆。");
        }

        var sessionLines = sessions
            .GroupBy(session => session.ProcessName)
            .Select(group => new
            {
                ProcessName = group.Key,
                Minutes = Math.Max(1, (int)Math.Round(group.Aggregate(TimeSpan.Zero, (total, session) => total + session.Duration).TotalMinutes)),
                Switches = group.Count()
            })
            .OrderByDescending(item => item.Minutes)
            .Take(10)
            .Select(item => $"- {item.ProcessName}: {item.Minutes}分钟, 切换{item.Switches}次")
            .ToList();
        if (sessionLines.Count == 0)
        {
            sessionLines.Add("- 最近24小时暂无进程切换记录。");
        }

        var prompt =
            "当前 RAG 检索上下文不足。请把已有本地记忆和最近24小时进程行为压缩成一条可再次检索的本地记忆摘要。\n" +
            "输出的 content 使用简短 Markdown，保留鱼骨结构：事件/任务主轴、tag 聚类、解释、预测、plan 完成情况。\n" +
            "不要虚构隐私细节；只基于输入信号做温和推断。\n\n" +
            $"用户当前问题：{userInput.Trim()}\n\n" +
            "已有本地记忆：\n" + string.Join('\n', memoryLines) + "\n\n" +
            "最近24小时进程行为：\n" + string.Join('\n', sessionLines) + "\n\n" +
            "必须只返回 JSON，不要 Markdown 代码块。格式：\n" +
            "{\"shouldRemember\":true,\"type\":\"Event\",\"title\":\"本地上下文压缩摘要\",\"content\":\"## 主轴\\n...\",\"tags\":[\"digest\",\"rag\"],\"weight\":0.72,\"memoryAxis\":\"event\",\"description\":\"压缩后的上下文摘要\",\"explanation\":\"用于上下文不足时补齐 RAG\",\"nextPrediction\":\"下次优先读取这条摘要再展开相关 plan\",\"isPlan\":false,\"isCompleted\":false,\"weightProfile\":\"{\\\"base\\\":0.72,\\\"digest\\\":0.30}\",\"reply\":\"已压缩本地上下文。\",\"confidence\":0.8}";

        var content = await SendOpenAiCompatiblePromptAsync(baseUri, prompt, cancellationToken, maxTokens: 800);
        return string.IsNullOrWhiteSpace(content)
            ? null
            : TryDeserializeJson<MemoryCandidateResult>(content);
    }

    public async Task<DesktopContextInsight?> CreateDesktopContextInsightAsync(
        string mode,
        string userInput,
        IReadOnlyCollection<ContextMemory> memories,
        IReadOnlyCollection<ApplicationUsageSession> sessions,
        ForegroundFocusSnapshot? foregroundSnapshot,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured ||
            !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var modeInstruction = mode switch
        {
            "replay" => "生成时间切片回放：按窗口/进程切换推测用户刚才在推进什么、卡在哪里。",
            "plan_progress" => "生成 plan 自动进度推断：只给建议，不自动标记完成；重点说明证据和不确定性。",
            "resume_scene" => "生成回到现场：帮助用户恢复最近工作现场，指出停在哪里、下一步打开什么。",
            "fishbone" => "生成任务-进程鱼骨归因：把进程行为按 plan/tag 聚类到主骨和分支。",
            "galaxy" => "生成星图解释：解释星座归类、阻塞点、未完成 plan 和节点关系。",
            "coding_review" => "生成 AI 编程完成检查：根据编码客户端状态、最近变更路径、计划和进程行为，提示可能影响面和验证步骤。",
            _ => "生成桌面上下文洞察：整合本地记忆、plan 状态和进程行为。"
        };

        var memoryLines = memories
            .OrderByDescending(memory => memory.IsPlan && !memory.IsCompleted && !memory.IsAbandoned)
            .ThenByDescending(memory => memory.UpdatedAt)
            .Take(30)
            .Select(memory =>
                $"- [{memory.Id}] {memory.Title} | type:{memory.Type} | axis:{memory.MemoryAxis} | plan:{memory.IsPlan}/{memory.IsCompleted}/abandoned:{memory.IsAbandoned} | lifecycle:{memory.Lifecycle} | constellation:{memory.ConstellationName} | tags:{memory.Tags} | next:{memory.NextPrediction} | content:{memory.Content}")
            .ToList();
        if (memoryLines.Count == 0)
        {
            memoryLines.Add("- 暂无本地记忆。");
        }

        var sessionLines = sessions
            .OrderBy(session => session.StartTime)
            .TakeLast(60)
            .Select(session =>
                $"- {session.StartTime:HH:mm:ss}-{session.EndTime:HH:mm:ss} | {session.ProcessName} | {Math.Max(1, (int)Math.Round(session.Duration.TotalMinutes))}分钟 | {session.ExecutablePath}")
            .ToList();
        if (sessionLines.Count == 0)
        {
            sessionLines.Add("- 暂无进程切换记录。");
        }

        var foreground = foregroundSnapshot == null
            ? "No current foreground snapshot."
            : $"{foregroundSnapshot.ProcessName} | {Math.Max(1, (int)Math.Round(foregroundSnapshot.Duration.TotalMinutes))}分钟 | {foregroundSnapshot.ExecutablePath}";

        var prompt =
            "你是 Perelegans 的 Win32 本地上下文分析层，也要像一个懂分寸的陪伴搭档。请解释本地记忆星图与进程传感数据，但不要把自己写成冷冰冰的监控报告。\n" +
            "只基于给定信号做温和推断；不要装作看到了文件内容；不要说教；不要使用 RPG/游戏叙事。\n" +
            "如果证据不足，明确说“证据不足”。下一步要贴合上下文，可以是验收、合并、搁置、回到某个现场或向用户确认，不要总写“拆成小动作”。\n" +
            "plan 记忆：未完成 plan 代表未来意图，已完成 plan 代表产出证据。进程切换只能作为间接证据，不能自动完成任务。\n\n" +
            $"模式：{mode}\n" +
            $"任务：{modeInstruction}\n" +
            $"用户输入：{userInput.Trim()}\n" +
            $"当前前台：{foreground}\n\n" +
            "本地记忆/plan：\n" + string.Join('\n', memoryLines) + "\n\n" +
            "进程时间切片：\n" + string.Join('\n', sessionLines) + "\n\n" +
            "返回 JSON only。字段含义：summary 是一段自然中文；evidence 是证据；planSuggestions 是计划完成/推进建议；fishbone 是鱼骨分支；constellationExplanations 是星图解释；suggestedNextAction 是下一步。语气可以亲近，但不要编造。\n" +
            "{\"summary\":\"...\",\"evidence\":[\"...\"],\"planSuggestions\":[\"...\"],\"fishbone\":[\"主骨：... / 分支：...\"],\"constellationExplanations\":[\"...\"],\"suggestedNextAction\":\"...\"}";

        var content = await SendOpenAiCompatiblePromptAsync(baseUri, prompt, cancellationToken, maxTokens: 1000, temperature: 0.48);
        return string.IsNullOrWhiteSpace(content)
            ? null
            : TryDeserializeJson<DesktopContextInsight>(content);
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
        int maxTokens = 300,
        double temperature = 0.2)
    {
        var requestUri = BuildChatCompletionsUri(baseUri);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settingsService.Settings.AiApiKey.Trim());

        var payload = new
        {
            model = _settingsService.Settings.AiModel.Trim(),
            temperature,
            max_tokens = maxTokens,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You are Perelegans' local-first assistant engine. Return compact JSON only, following the requested schema exactly."
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

    private static string NormalizePersonalityPrompt(string? prompt)
    {
        return string.IsNullOrWhiteSpace(prompt)
            ? AppSettings.DefaultAiPersonalityPrompt
            : prompt.Trim();
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

        foreach (var candidate in CreateJsonCandidates(json))
        {
            try
            {
                return JsonSerializer.Deserialize<T>(candidate, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static string? ExtractJsonObject(string text)
    {
        var normalized = text.Trim();
        if (normalized.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineBreak = normalized.IndexOf('\n');
            var lastFence = normalized.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLineBreak >= 0 && lastFence > firstLineBreak)
            {
                normalized = normalized[(firstLineBreak + 1)..lastFence].Trim();
            }
        }

        var start = normalized.IndexOf('{');
        var end = normalized.LastIndexOf('}');
        return start >= 0 && end > start
            ? normalized[start..(end + 1)]
            : null;
    }

    private static IEnumerable<string> CreateJsonCandidates(string json)
    {
        yield return json;

        var withoutTrailingCommas = json
            .Replace(",}", "}", StringComparison.Ordinal)
            .Replace(",]", "]", StringComparison.Ordinal);
        if (!string.Equals(json, withoutTrailingCommas, StringComparison.Ordinal))
        {
            yield return withoutTrailingCommas;
        }
    }
}
