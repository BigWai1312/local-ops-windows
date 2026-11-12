# 本地运维台 Windows 执行与发布计划

> 当前状态：2.1.0 已接入 C# / WPF、版本目录安装器和 GitHub 更新器。发行以当前测试、安装包验证和远端 CI 为准；剩余验收见 `csharp/FEATURE_PARITY.md`。下文的历史实现记录保留用于追溯。

## 1. 目标与边界

- 本地目录：`local-ops-windows/`
- 产品名：本地运维台 Windows（Local Ops Windows）
- 目标平台：Windows 10/11 x64
- 运行时：安装版为自包含 .NET 10 WPF 客户端 + PyInstaller 标准库后台和锚点；网页兼容入口无构建、无 CDN
- 网络边界：仅绑定 `127.0.0.1`
- 上游来源：`laogou717/local-ops`，MIT License

本项目是明确署名的 Windows-only 二次开发。改名不改变上游版权归属，也不用于规避来源说明。原始许可证、第三方许可和 PR 贡献记录必须保留。

## 2. 已采用的上游工作

### PR #2：Windows 功能基线

作为共享前后端和 Windows 启动/测试的基础。已针对本机验证结果修正 CIM 权限、端口扫描、本地化输出和外部进程控制问题。

### PR #3：安全设计参考

采用 fail-closed、受控身份、严格进程所有权和安全发行思路。未引入其 `pywin32`/打包运行时依赖；当前实现使用标准库 `ctypes`、随机 run token、锚点 PID 和经过验证的 PPID 成员快照。

### PR #4：API 与启动器参考

参考 Windows API 结构和隐藏窗口启动方式，但不维护第二套 `server_win.py`，避免前后端契约分叉。

## 3. 已完成实现

- 将项目放入独立子目录 `local-ops-windows/`，原 `local-ops/` 不修改。
- 产品、页面、启动器、数据目录和发行包改名为 Local Ops Windows。
- 数据目录改为 `%APPDATA%\LocalOpsWindows`，日志改为 `%LOCALAPPDATA%\LocalOpsWindows\logs`。
- 使用 `GetExtendedTcpTable` 读取 TCP 监听端口，不依赖管理员权限和本地化命令输出。
- 使用 Toolhelp、`NtQueryInformationProcess`、`GetProcessTimes`、`GetProcessMemoryInfo` 和 `GlobalMemoryStatusEx` 构建 Windows 进程快照。
- 使用 `msvcrt.locking` 实现 Windows 单实例写锁。
- 使用 `tools/win_anchor.py`、随机 run token 和 PPID 树识别本程序启动的进程。
- 停止时只对已验证成员调用 `OpenProcess(PROCESS_TERMINATE)`/`TerminateProcess`；不按端口、进程名或裸 PID 杀进程。
- HTTP 后台禁止外部进程认领和任意外部进程结束；网页按 `capabilities` 隐藏相关入口。C# 预览增加独立的手动端口冲突处理：确认目标、复核创建身份、按需一次性 UAC；不认领、不自动重放或结束外部进程树。
- 新增 `start_console_win.vbs` 隐藏窗口启动器、Windows 测试、隐私扫描和可重复发行包检查。
- 新增 `desktop.py`：WebView2 窗口、单实例唤醒、托盘生命周期、窗口状态记忆和缺失运行时提示。
- 安装包快捷方式直接指向桌面客户端 EXE；关闭窗口默认隐藏到托盘，只有托盘退出才停止本地服务。
- 2.0.2 安装器能够停止并移除本产品 2.0.x `app\\` 下的 Tk 客户端，再安装根目录单文件 WebView2 客户端；不会触碰其他项目或 Docker 目录。
- 新增 `NOTICE.md`，明确上游来源、许可证、PR 参考和二开范围。

## 4. Windows-only 收敛

发行范围保留：

```text
server.py
start.bat
start_console_win.vbs
static/
tools/
tests/
README.md
NOTICE.md
LICENSE
```

不进入 Windows 发行物：

```text
总控台.app/
start.command
Info.plist
AppIcon.icns
iconutil 生成逻辑
用户配置、日志、缓存、数据库和本地构建目录
```

## 5. 验证计划

1. 运行 `python -m unittest tests.test_windows tests.test_desktop -v`，验证真实服务、桌面入口、单实例和 WebView2 缺失处理。
2. 运行 `python tools/check_privacy.py`，拒绝个人路径、邮箱、密钥、令牌和敏感文件。
3. 运行 `python tools/check_project.py`，验证 Python/JavaScript、静态资源、Windows 启动器和测试。
4. 运行 `python tools/build_release.py --dist dist`，再用 `--verify-only` 校验归档与 SHA-256。
5. 从解压后的最终 zip 启动，验证 `/api/health`、`/api/state`、日志和停止流程。
6. 在 GitHub Actions `windows-latest` 上重复项目检查、隐私扫描、冒烟测试和可重复构建。

## 6. 隐私与发布门禁

- 不提交 `%APPDATA%`/`%LOCALAPPDATA%` 中的运行数据。
- 不提交真实命令、用户名、邮箱、机器名、个人绝对路径、Cookie、token、API key 或 SSH key。
- 示例路径只使用 `C:\Users\example\project`。
- 发布前检查 Git 作者/提交者信息，避免使用隐私邮箱；可使用 GitHub noreply 邮箱。
- README、Issue/PR 模板、截图和发行说明必须脱敏。

## 7. GitHub 插件发布方案

GitHub 插件负责仓库读取、分支提交、远端核对和草稿 PR 创建；本地 Git 负责工作树审计与提交校验。

当前发布目标已经确定为公开 Fork `BigWai1312/local-ops-windows`，不向上游 `laogou717/local-ops` 推送。Windows 改动位于 `agent/windows-local-ops` 分支，目标基线为 Fork 的 `main`，以便保留上游来源并让衍生项目的变更可追溯。

草稿 PR 必须说明：

- 上游仓库、MIT License 和衍生项目性质；
- PR #2/#3/#4 的采用与差异；
- Windows-only 改造、产品改名和安全边界；
- 隐私扫描、测试命令、发行包校验值和已知限制；
- 不包含本地配置、日志、凭据或个人路径。

## 8. 完成定义

- Windows 10/11 普通用户可启动和使用。
- Windows 真实生命周期测试、项目检查和隐私扫描全部通过。
- Windows zip 可重复构建并通过解压后冒烟测试。
- 仓库保留上游许可证和 NOTICE，变更记录可追溯。
- GitHub Fork、目标分支和草稿 PR 已创建，PR 描述包含验证结果与隐私声明。

## 9. C# 客户端交付计划

1. 按 `csharp/FEATURE_PARITY.md` 对照网页功能逐项验收，保留受控进程边界。
2. 使用 WPF 单实例、托盘、窗口记忆、命令面板与任务通知；隐藏时暂停界面轮询。
3. 自包含打包 .NET、Python 后台、锚点和对应许可，不要求安装 Python、.NET SDK 或 WebView2。
4. GitHub 更新资产只来自个人 Fork，下载后校验摘要；资源格式禁止脚本、DLL 和 XAML。
5. 运行 C# 测试及 `tools/verify_installer.py`，验证内嵌 payload、解压代码隐私、隔离安装、真实快捷方式和注册表、冻结后台生命周期、卸载保留数据，再核对原生窗口实际显示与操作。
6. 使用不含私人邮箱历史的发布提交更新个人 Fork；CI 通过后创建版本标签，发布并回读下载资产校验摘要。
