# Local Ops Windows WPF Preview

This is a separate native WPF desktop sample with fictional data, not an HTML page or WebView. It does not reference the production client, connect to the management backend, start or stop real processes, or modify production settings.

Run it from the repository root:

```powershell
dotnet run --project csharp/LocalOpsWindows.Preview/LocalOpsWindows.Preview.csproj
```

The sample uses the same spacing, color palette and Lucide assets as the earlier visual draft. It includes 17 projects, an adaptive card launchpad with a single-column list switch, working search and filters, a card-width slider, simulated start/stop, an in-memory editor, service tabs and selection, native resource charts, log filtering, compact settings, and light/dark/system themes.

The card-size slider sets the desired minimum width from 300 to 480 logical pixels. Cards fill each row evenly and change column count as needed, using the client's existing wrap-panel approach with a preview-only width property. Height remains 252 pixels for cards and 148 for list rows, independent of this slider. Notes have two readable lines, commands have up to two lines, and directories remain visible. Overflow is trimmed with full text in tooltips and the information dialog. The in-memory editor supports multiline notes; search and export include them. Switching layouts preserves search, filters and monitor selections.

The launchpad includes a compact performance area: no selection shows totals for registered projects, clicking a card shows one project's details, and checkboxes add projects to a comparison. Clicking an action button does not change the selection. CPU and memory donut gauges show summed percentages of machine capacity, not per-project shares or whole-machine telemetry. Only running projects contribute. Selected projects have separate trend lines, with CPU/memory switching for comparisons; more than four projects show three individual lines plus an explicitly labelled remainder. Clearing selection returns to the overview. All samples and history are fictional, deterministic preview data, not collected system measurements.

At widths below 1180 or heights below 680 logical pixels the launchpad's right-side monitor becomes a compact top strip. Single-project full information remains accessible with its document icon. The project list scrolls independently; the monitor itself does not require paging.

The service detail panel stays on the right and hides below 1200 logical pixels in width or 720 in height. Short windows omit the trend chart instead of scrolling the detail. The title bar uses WPF WindowChrome with real minimize, maximize/restore and close commands.

Only theme, card size, card/list layout and log preferences are saved in `data/preview-preferences.json` beside the preview executable. Older preferences retain their theme/layout/log settings and default to 320-pixel card size; the old height-only density setting is no longer used. Example project changes and monitor selections last for the current run. Autostart, notifications, backend maintenance and update checks are simulations with no operating-system or network effects. The system theme is read when selected or on startup.

Keep the executable with its DLLs and `Assets` directory. The sample requires .NET 10 Desktop Runtime. Lucide icons are rendered using native WPF geometries; original notices are in `licenses/`.

Run the off-screen smoke check without opening a window:

```powershell
dotnet run --project csharp/LocalOpsWindows.Preview.Tests/LocalOpsWindows.Preview.Tests.csproj
```

The test writes native RenderTargetBitmap screenshots to `tmp/wpf-preview-size-monitor/`, checks five desktop sizes in both themes across four pages and both launchpad layouts (50 baseline layouts), and verifies adaptive columns, non-overlapping cards/headings/monitor, single-column rows, fixed heights with variable widths, notes, long text, selection preservation, aggregate values, trend data and metric switching, bounded comparison series, stopped-project zero values, donut boundaries, log visibility and an in-memory add/start/stop flow. It never calls `Show` or creates a native desktop window. Display scaling, desktop compositor effects and window dragging still need manual acceptance.

If the standard preview executable is open, build without closing it:

```powershell
dotnet build csharp/LocalOpsWindows.Preview/LocalOpsWindows.Preview.csproj -c Release --no-restore -p:OutputPath=bin\CardPreview\ -p:IntermediateOutputPath=obj\CardPreview\
dotnet run --project csharp/LocalOpsWindows.Preview.Tests/LocalOpsWindows.Preview.Tests.csproj -c Release -p:OutputPath=bin\CardPreview\ -p:IntermediateOutputPath=obj\CardPreview\
```

The alternate executable is `csharp/LocalOpsWindows.Preview/bin/CardPreview/LocalOpsWindows.Preview.exe`. It keeps its own preview preferences beside that executable; no production data is copied.

## Portable Preview EXE

The width slider is labelled "卡片宽度"; it controls width and column count, not proportional scaling. The Windows x64 portable preview includes its own .NET runtime:

```powershell
dotnet publish csharp/LocalOpsWindows.Preview/LocalOpsWindows.Preview.csproj -c Release -r win-x64 --self-contained true --artifacts-path tmp/preview-exe-build -o dist/wpf-ui-preview-win-x64 -p:DebugType=None -p:DebugSymbols=false
```

Run `dist/wpf-ui-preview-win-x64/LocalOpsWindows.Preview.exe`. Keep it with all published files, including `Assets/Icons` and `licenses`. This is an interface preview, not an installer or the production service manager. It requires no separate .NET installation, writes preview preferences beside the EXE, and leaves production data untouched.
