# Perelegans

Perelegans 是一个本地优先的 Windows 桌面专注助手。它以悬浮宠物的形式常驻桌面，记录前台应用使用情况，通过 AI 理解你的工作上下文，并在本地构建一张可交互的记忆星图。

## 功能概览

### 悬浮助手
- 启动后显示小型悬浮宠物窗口，常驻桌面顶层
- 气泡实时显示当前应用的专注状态提示
- 双击打开主面板，右键菜单快速访问常用操作
- 支持拖拽移动，不干扰正常工作

### 应用监控
- 基于 Windows 前台窗口和进程信息，自动记录每个应用的使用时长和切换次数
- 使用 SQLite 本地持久化，数据不离开本机
- 支持暂停/恢复监控
- 主面板展示当前前台应用、累计时长和最近使用记录

### AI 专注判断
- 接入 OpenAI 兼容接口，根据进程名和停留时长判断当前应用是否有助于专注
- 支持自定义专注目标（如"考研复习"、"写论文"、"开发项目"），AI 会围绕该目标进行判断
- 支持截图分析：通过视觉模型判断当前桌面是否处于专注状态
- 未配置 AI 时，使用内置生产力应用列表进行基础判断

### 记忆星图
- 对话中提到的偏好、项目、决定、计划、工作流等信息，由 AI 自动提取并保存为本地记忆节点
- 记忆节点按星座分类，在主面板以可交互的星图（Galaxy）或鱼骨图（Fishbone）形式展示
- 每个节点包含：标题、内容、类型、标签、AI 描述、解释、下一步预测、计划状态、权重
- 支持手动编辑、保存和删除记忆节点
- 支持专注模式：从记忆节点启动专注，追踪当前任务上下文

### AI 对话
- 主面板内置对话框，可直接与 Perelegans 对话
- AI 回复时会引用本地记忆和当前桌面上下文，给出具体、有依据的回答
- 对话中识别到的任务意图会自动解析为可执行任务
- 预设快捷提示：深度工作、学习、写作、编程

### 任务系统
- 从对话或手动输入创建任务，AI 自动生成任务洞察（目标摘要、下一步动作、难度、预计时长、星座归类）
- 任务完成时生成复盘总结
- 任务与记忆节点关联，形成可追溯的上下文链路

### 桌面上下文分析
- 多种分析模式：时间切片回放、计划进度推断、回到现场、鱼骨归因、星图解释
- 综合本地记忆、计划状态和进程切换行为，推断用户当前在推进什么、卡在哪里
- 日报功能：汇总今日任务、记忆星图和应用使用情况，生成简短复盘

### 系统集成
- 单实例运行保护，再次启动时激活已有实例
- 支持开机自启动
- 支持关闭主面板后最小化到系统托盘
- 支持数据库备份和恢复

## 环境要求

- Windows 10 1903 (19041) 或更高版本
- 开发构建需要 .NET 8 SDK

## 构建

```powershell
dotnet build src\Perelegans\Perelegans.csproj
```

## 运行

```powershell
dotnet run --project src\Perelegans\Perelegans.csproj
```

启动后显示悬浮助手。双击悬浮助手打开主面板。

## AI 配置

AI 功能为可选项。未配置时，Perelegans 仍会记录应用使用情况并进行基础专注判断。

在设置窗口的 **AI** 页填写：

| 字段 | 说明 |
|------|------|
| API Base URL | OpenAI 兼容接口地址，例如 `https://api.openai.com/v1` |
| API Key | 接口密钥 |
| 模型名称 | 例如 `gpt-4o`、`gpt-4o-mini` |
| 专注目标 | 可选，告诉 AI 当前围绕什么任务判断，留空则按通用专注处理 |

截图分析功能需要支持视觉输入的模型（如 `gpt-4o`）。

如需代理，在同一设置页配置 HTTP 代理地址。

## 数据存储

所有数据保存在本机 `%LocalAppData%\Perelegans\` 目录下：

| 文件 | 内容 |
|------|------|
| `settings.json` | 应用设置 |
| `perelegans.db` | SQLite 数据库 |
| `error.log` | 崩溃日志（可用时写入） |

数据库表：

| 表 | 内容 |
|----|------|
| `ApplicationUsages` | 应用累计使用记录 |
| `ApplicationUsageSessions` | 每次前台会话记录 |
| `FocusTasks` | 任务列表 |
| `FocusTaskLinks` | 任务关联关系 |
| `ContextMemories` | 本地记忆节点 |

## 项目结构

```text
src/Perelegans/
  App.xaml(.cs)              应用启动、单实例、托盘和窗口组装
  Data/                      EF Core 数据库上下文
  Models/                    数据模型（应用使用、记忆节点、任务、AI 结果等）
  Services/
    DatabaseService           数据库读写
    ProcessMonitorService     前台进程监控
    FocusClassificationClient AI 接口调用（专注判断、记忆提取、对话、任务分析、日报）
    ContextRetrievalService   记忆检索与上下文打包
    MemoryExtractionService   记忆候选提取与保存
    FocusModeService          专注模式状态管理
    SettingsService           设置读写
    ThemeService              主题切换
    LocalizationService       多语言
  ViewModels/                 悬浮助手、主面板、设置窗口的 ViewModel
  Views/                      悬浮助手、主面板、设置窗口
  Themes/                     亮色和暗色主题资源字典
  i18n/                       多语言资源文件（中文、英文）
```

## 技术栈

- **框架**：WPF on .NET 8，目标平台 Windows 10 19041+
- **UI 库**：MahApps.Metro
- **MVVM**：CommunityToolkit.Mvvm
- **数据库**：SQLite via Entity Framework Core
- **AI**：OpenAI 兼容 chat completions 接口

## 许可证

MIT
