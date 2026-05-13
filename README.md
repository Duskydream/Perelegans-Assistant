# FocusArchive

FocusArchive is a Windows desktop focus companion rebuilt from Perelegans.
It records foreground application usage, shows a small floating agent, and can use an OpenAI-compatible model to classify whether the current app is likely helping or distracting.

This repository is currently in an active refactor. The old visual novel library, game metadata, recommendation, cover, VNDB, Bangumi, and ErogameSpace features have been removed from the main application path.

## Current Features

- Floating focus agent that starts with the app.
- Double-click the floating agent to open the dashboard.
- Foreground application monitoring through Windows process APIs.
- Local SQLite storage for application usage totals and recent sessions.
- Dashboard for current foreground app, total tracked time, focus category counts, and recent sessions.
- Optional AI focus classification through an OpenAI-compatible chat completions endpoint.
- Theme selection, language selection, HTTP proxy setting, monitor interval, launch-at-startup, and close behavior settings.
- Single-instance protection with activation of the existing app window.
- Optional minimize-to-tray behavior.
- Database backup and restore for the focus usage database.

## Requirements

- Windows 10 or newer.
- .NET 8 SDK for development builds.

## Build

```powershell
dotnet build src\Perelegans\Perelegans.csproj
```

## Run

```powershell
dotnet run --project src\Perelegans\Perelegans.csproj
```

The app starts as a small floating focus agent. Double-click it to open the FocusArchive dashboard.

## AI Configuration

AI classification is optional. Without it, FocusArchive still records foreground usage and uses a small built-in list of known productivity applications.

To enable AI classification, open Settings and fill in:

- API base URL
- API key
- Model name

The current classifier expects an OpenAI-compatible chat completions API. HTTP proxy settings can be configured in the same Settings window.

## Data

FocusArchive stores local data under the user's local application data directory:

- `settings.json` for application settings
- `perelegans.db` for SQLite usage history
- `error.log` for crash logs when available

The current database schema keeps only:

- `ApplicationUsages`
- `ApplicationUsageSessions`

During startup, legacy game-library tables are dropped if they exist so that older local databases do not keep obsolete structures around.

## Project Layout

```text
src/Perelegans/
  App.xaml(.cs)              Application startup, single instance, tray, window wiring
  Data/                      EF Core database context
  Models/                    Focus usage, settings, and theme models
  Services/                  Monitoring, storage, AI classification, settings, theme, translation
  ViewModels/                Floating agent, dashboard, and settings view models
  Views/                     Floating agent, dashboard, and settings windows
  Themes/                    Light and dark theme resources
  i18n/                      Localized strings
```

## Development Notes

This branch intentionally favors a smaller, cleaner surface over compatibility with the old game tracker. When adding new functionality, keep it aligned with the focus tracking direction and avoid reintroducing game-library concepts.

## License

MIT
