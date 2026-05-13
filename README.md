# re-Perelegans

re-Perelegans 是一个 Windows 桌面专注辅助工具，由原 Perelegans 重构而来。
它会记录当前前台应用的使用情况，提供一个常驻悬浮助手，并可接入 OpenAI 兼容接口来判断当前应用更偏向专注工作还是分心娱乐。

当前项目仍处在重构阶段。旧版视觉小说库、游戏元数据、推荐、封面、VNDB、Bangumi、ErogameSpace 等功能已经从主应用路径中移除。

## 当前功能

- 启动后显示悬浮专注助手。
- 双击悬浮助手可打开主面板。
- 基于 Windows 前台窗口和进程信息记录应用使用情况。
- 使用 SQLite 本地保存应用累计时长和最近会话。
- 主面板展示当前前台应用、总记录时长、专注分类数量和最近使用记录。
- 可选接入 OpenAI 兼容的 chat completions 接口进行 AI 专注判断。
- 支持主题、语言、HTTP 代理、监控间隔、开机启动和关闭行为设置。
- 单实例运行保护，再次启动时会激活已有实例。
- 支持关闭主面板后最小化到托盘。
- 支持专注使用数据库的备份和恢复。

## 环境要求

- Windows 10 或更高版本。
- 开发构建需要 .NET 8 SDK。

## 构建

```powershell
dotnet build src\Perelegans\Perelegans.csproj
```

## 运行

```powershell
dotnet run --project src\Perelegans\Perelegans.csproj
```

应用启动后会显示一个小型悬浮助手。双击悬浮助手即可打开 re-Perelegans 主面板。

## AI 配置

AI 专注判断是可选功能。即使不配置 AI，re-Perelegans 仍会记录前台应用使用情况，并使用内置的常见生产力应用列表进行基础判断。

如需启用 AI 判断，请在设置窗口中填写：

- 专注目标（可留空）
- API Base URL
- API Key
- 模型名称

专注目标用于告诉 AI 当前判断应围绕什么任务展开，例如通用专注、考研复习、写论文或开发项目。留空时会按通用专注判断。
当前专注判断模块使用 OpenAI 兼容的 chat completions 接口。如果需要代理，可以在同一个设置窗口里配置 HTTP 代理。

## 数据

re-Perelegans 会把本地数据保存在用户的本地应用数据目录下：

- `settings.json`：应用设置
- `perelegans.db`：SQLite 使用记录数据库
- `error.log`：可用时写入崩溃日志

当前数据库只保留两张核心表：

- `ApplicationUsages`
- `ApplicationUsageSessions`

启动时，如果检测到旧版游戏库相关表，会自动删除这些遗留表，避免旧结构继续污染新版本。

## 项目结构

```text
src/Perelegans/
  App.xaml(.cs)              应用启动、单实例、托盘和窗口组装
  Data/                      EF Core 数据库上下文
  Models/                    应用使用记录、设置、主题和 AI 判断模型
  Services/                  监控、存储、AI 判断、设置、主题和多语言服务
  ViewModels/                悬浮助手、主面板和设置窗口的 ViewModel
  Views/                     悬浮助手、主面板和设置窗口
  Themes/                    亮色和暗色主题资源
  i18n/                      多语言资源文件
```

## 开发说明

这次重构有意缩小项目表面积，优先保证新方向清晰可维护，而不是兼容旧版游戏库功能。
后续新增功能应围绕专注追踪、应用使用分析和桌面辅助体验展开，避免重新引入游戏库领域概念。

## 许可证

MIT
