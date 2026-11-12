# 本地运维台 Windows
`Local Ops Windows` 是一个面向 Windows 10/11 x64 的本地服务监控与任务启动台。2.1.0 安装版使用 C# / WPF 原生窗口，随包提供 .NET 与 Python 后台，不依赖 WebView2。

> 当前版本处于 Preview / Alpha。它可以执行用户保存的本地命令，不应暴露到公网、反向代理、端口映射或不受信任的用户环境。

## 二开说明

本项目基于 [laogou717/local-ops](https://github.com/laogou717/local-ops) 进行 Windows-only 二次开发，遵循上游 MIT License，并保留第三方素材和许可证记录。

本版本主要改动：

- 产品名改为“本地运维台 Windows / Local Ops Windows”。
- 仅支持 Windows 10/11，不再把 macOS 启动器和发行文件纳入交付范围。
- 复用上游 PR #2 的 Windows 适配，并修复端口扫描、PowerShell 日期解析、任务锚点和外部进程控制边界。
- 参考 PR #3 的安全模型：受控 token、进程创建身份、进程树校验和 fail-closed。
- 参考 PR #4 的 Windows API 对齐和隐藏窗口启动方式，但不维护第二套独立后端。
- 增加 Windows 测试、隐私扫描、发行包审计和明确的二开归属说明。
- 增加 `LocalOpsWindows-Setup.exe`：按当前用户安装、开始菜单/桌面快捷方式、HKCU 开机自启和卸载入口。
- 设置中心支持检查个人 GitHub 仓库 Releases，下载后校验 SHA-256 并交给安装器完成更新。
- 安装版是 C# 原生客户端，提供启动台、端口监控、日志中心、设置、主题、命令面板和任务通知。
- 项目编辑支持脚本选择、全部识别候选、图标上传/粘贴与 favicon；监控包含实际端口、IPv6、发现列表、动态和资源趋势。
- 支持窗口尺寸/位置记忆、显式最小化到通知区域、托盘显示/退出、单实例唤醒和开机隐藏启动。

完整范围见 [`PLAN.md`](PLAN.md) 和 [`NOTICE.md`](NOTICE.md)。

## 功能

- 查看本机监听端口、进程、CPU、内存、运行时长和启动来源。
- 保存常用服务或批处理任务，集中启动、停止、重启、查看日志和诊断。
- 从项目目录识别 Node、Python、Go、Rust、Docker Compose 和静态站点命令。
- 运行前检查工作目录、脚本、运行时和端口占用。
- 使用随机 token 和受控锚点识别本程序启动的进程树。
- 网页版不认领或结束外部进程。C# 客户端支持手动核实并强停项目端口的外部占用者，具体边界见 `csharp/README.md`。

## 系统要求

- Windows 10/11 x64。
- Windows PowerShell 5.1 或 PowerShell 7。
- 安装版已包含 .NET 10 Windows Desktop Runtime 和 Python 运行时。

源码调试和发行构建需要 .NET 10 SDK、Python 3.12+；用户保存的项目仍需要其自身的运行环境。

## 安装版

从个人仓库的 GitHub Release 下载 `LocalOpsWindows-Setup.exe`，双击后按当前用户安装到：

```text
%LOCALAPPDATA%\Programs\LocalOpsWindows
```

安装器会创建开始菜单和桌面快捷方式，并默认写入当前用户的开机自启项。若检测到旧版源码部署使用的同名计划任务 `Local Ops Windows`，安装器会删除该任务，避免登录时重复启动。它不要求管理员权限，也不会修改 Docker 项目、系统服务或已有端口。卸载可从 Windows“应用和功能”进入，或运行安装目录中的 `LocalOpsWindows-Setup.exe --uninstall`。

安装完成后，双击开始菜单或桌面快捷方式会打开客户端；开机自启使用隐藏窗口与通知区域模式。安装器先校验全部文件，再写入独立的 `versions/<版本>-<摘要>/` 目录，通过 `current.json` 和快捷方式切换版本。切换失败时恢复原快捷方式、注册表和当前版本；旧版本与用户配置不会被覆盖删除。

当前公开安装包未做商业代码签名，Windows SmartScreen 可能显示“未知发布者”。Release 同时提供 `.sha256` 文件；安装前可用 `Get-FileHash` 核对，但这不能替代正式代码签名。

## 启动

### 桌面客户端启动

双击：

```text
start_console_win.vbs
```

### 调试启动

双击 `start.bat`，或在 PowerShell 中运行：

```powershell
.\start.bat
```

可选参数：

```powershell
dotnet run --project csharp/LocalOpsWindows.Client -- --demo
py -3 server.py --no-browser --preferred-port 9603
```

桌面客户端会在 `127.0.0.1:9600-9609` 的一个空闲端口连接或启动标准库后台，通过 HTTP API 读取数据。`static/` 网页版保留作为兼容及开发入口；`desktop.py` 是历史 WebView2 宿主，不进入 C# 安装版。

安装版支持 `LocalOpsWindows.Client.exe --background`，供开机自启使用；普通启动不加该参数会打开桌面客户端窗口。

## 数据与日志

默认位置：

| 内容 | 路径 |
| --- | --- |
| 配置和图标 | `%APPDATA%\LocalOpsWindows\` |
| 日志 | `%LOCALAPPDATA%\LocalOpsWindows\logs\` |

可用环境变量覆盖：

```powershell
$env:CONSOLE_DATA_DIR = 'D:\LocalOpsData'
$env:CONSOLE_LOG_DIR = 'D:\LocalOpsLogs'
.\start.bat
```

覆盖值必须是非空绝对路径，并指向专用子目录；不要使用磁盘根目录、用户主目录或项目根目录。

## 安全边界

- 内部后台只绑定回环地址，不监听局域网或公网地址；端口不固定，且由单实例锁保护。
- Windows 版不开放外部进程认领和任意进程结束接口。
- 常规停止/重启只作用于带当前随机 token、匹配受控锚点和进程树的应用；C# 手动端口冲突处理另有本机确认与进程身份复核，不开放 HTTP 强停接口。
- 端口、PID、名称、cwd 和 PPID 都不能单独作为所有权证明。
- 配置使用临时文件、原子替换和上一份良好备份。
- 不要把配置、日志、真实命令、截图或个人绝对路径上传到公开仓库。

更多信息见 [`SECURITY.md`](SECURITY.md)。

## 检查与测试

Windows 权威检查：

```powershell
python tools/check_project.py
```

只运行 Windows 测试：

```powershell
python -m unittest tests.test_windows -v
```

运行隐私扫描：

```powershell
python tools/check_privacy.py
```

构建发行包：

```powershell
python tools/build_release.py --dist dist
python -m pip install -r requirements-build.txt
python tools/build_installer.py --output dist
python tools/verify_installer.py --payload build/csharp-installer/payload --setup dist/LocalOpsWindows-Setup.exe
```

如需验证旧 WebView 单文件安装包升级，在 Python 3.12 下给最后一条命令追加 `--legacy-setup path/to/trusted-old-setup.exe`。此选项执行可信旧包内的安装代码和程序，在独立临时目录、专用 HKCU 测试键及临时快捷方式中检查升级与失败回滚，不替换当前用户的正式安装。它不覆盖旧版客户端窗口交互或登录自启的实际桌面验收。

GitHub Actions 在每次推送或 PR 时运行检查；匹配 `VERSION` 的 `v<version>` 标签通过检查后才创建 Release，已存在的发行资产不会覆盖。应用内更新只信任仓库 `BigWai1312/local-ops-windows` 的 HTTPS Release 资产，并校验更新清单中的 SHA-256。

客户端或后台代码更新通过安装包完成。兼容资源更新仅支持已列入白名单的主题颜色 JSON，校验版本范围和递增修订号后即时应用，并保留可回退缓存；不下载执行脚本、DLL 或 XAML。资源发行标签为 `v<version>-resources.<revision>`，仍须附带安装包，供旧客户端升级使用。

## 隐私与发布

提交或发布前必须确认仓库和发行物不包含：

- `config.json`、日志、数据库、缓存和备份；
- `.env`、token、Cookie、API key、SSH key 或其他凭据；
- 真实用户名、机器名、邮箱、个人绝对路径和真实业务命令；
- `.git/`、`.venv/`、`node_modules/`、`data/`、`dist/` 等本地产物。

示例路径统一使用 `C:\Users\example\project`。

## 许可证

项目代码沿用上游 MIT License。Lucide、Geist Mono 和品牌素材可能适用各自许可证或来源要求，详见：

- [`LICENSE`](LICENSE)
- [`NOTICE.md`](NOTICE.md)
- [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)
- [`ASSET_PROVENANCE.md`](ASSET_PROVENANCE.md)
