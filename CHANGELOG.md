# Changelog

## Unreleased

## 2.1.0

- 安装版切换为自包含 .NET 10 WPF 客户端，使用冻结的标准库 Python 后台与锚点，无需 WebView2、系统 Python 或 .NET SDK。
- 补齐脚本选择、命令候选、图标操作、实际端口/IPv6、诊断修复、监控动态与趋势、命令面板、任务通知和窗口记忆。
- 接入个人 GitHub Fork 更新：代码更新使用 SHA-256 校验后的安装包；兼容的声明式主题资源即时应用并保留回退缓存。
- 版本目录安装保留旧文件和用户数据，失败回滚快捷方式与注册表；新增内嵌 payload、解压代码隐私与真实隔离安装验证。
- 修复冻结后台重启参数和 PowerShell 卸载清理进程未执行的问题。

- C# 客户端新增“处理占用”：查看实时占用进程并确认强停；复核创建时间、路径、用户、会话和监听关系，只结束单个目标，拒绝访问时可请求一次性 UAC，不开放 HTTP 任意杀进程接口。
- 修复 C# 客户端在大量监听服务下卡在“正在连接”：轻量健康响应携带后台 PID，新客户端不再为身份确认触发完整扫描；兼容旧后台的慢状态响应。
- 降低 C# 前台刷新开销：可见窗口每 5 秒同步、隐藏/最小化时暂停轮询，返回后立即同步；仅重建当前页面列表。
- 修复 C# WPF 客户端后台退出后持续连接失效端口的问题：首次连接失败和运行中断线均自动恢复，后台重启后重新探测端口，断线标记旧数据且不重放项目启停请求；保留后台日志与共享后台退出边界。
- C# WPF 预览版增加统一应用图标，用于 EXE、窗口、侧栏和托盘；补齐项目菜单、服务操作及编辑器图标，不新增运行时依赖。

## 2.0.2

### Changed

- 恢复原有完整网页控制台：安装版重新以 `pywebview + WebView2` 在独立窗口中渲染 `static/` 前端，不再使用 Tk 重写界面。
- 保留开始菜单、桌面快捷方式、开机后台启动、通知区域、单实例和 GitHub 更新能力；客户端不打开外部浏览器或显示地址栏。
- 安装器可从 2.0.x 的 `app\\` 原生客户端目录覆盖升级，并清理该产品目录下不再使用的旧运行文件。

## 2.0.1

### Changed

- 原生客户端重做为完整启动台界面：深色导航轨、顶部状态区、项目概览、服务/任务卡片、端口监控、日志中心和设置页，窗口布局面向桌面尺寸收敛。
- 默认窗口调整为 `1240x780`，最小尺寸调整为 `980x650`，在不引入浏览器运行时的前提下保留原网页启动台的信息架构。

### Fixed

- Windows 客户端右上角关闭按钮和 `Alt+F4` 现在会真正退出，不再因为托盘不可用而无法结束进程。
- 增加“最小化到托盘”和“退出”操作；开机后台启动在通知区域不可用时会自动显示完整主窗口，避免出现空白或不可达窗口。
- 兼容旧版本遗留的不可见互斥体；旧窗口无法唤醒时，修复后的客户端会自动恢复启动，不再被错误判定为重复实例。

## 2.0.0

### Added

- 标准库原生 Windows 客户端：`tkinter/ttk` 窗口、Win32 通知区域、单实例 Mutex、窗口状态记忆和设置中的开机自启开关。
- 项目、端口、日志、受控进程启动/停止/重启、添加项目和 GitHub 更新检查均直接调用受控业务层，不再经过 HTTP API。
- `--onedir` 主程序打包及对应安装器部署逻辑，安装包仍为单个 `LocalOpsWindows-Setup.exe`。

### Changed

- 正常启动不再创建 `127.0.0.1:9600-9609` 监听器；第二次启动只唤醒已有原生窗口。
- 移除 WebView2 和第三方托盘运行时，降低常驻内存与安装体积。

### Removed

- 移除 `pywebview`、`pystray`、WebView2 Runtime 依赖，以及正常客户端中的浏览器/网页渲染路径。

## 1.2.1

### Fixed

- 修复 PyInstaller `--windowed` 客户端以 `--background` 启动时，因无效控制台输出句柄触发 `[Errno 22] Invalid argument` 的问题。
- 后台日志现在直接使用 UTF-8 文件流，不再依赖 GUI 进程中不存在的标准控制台句柄。

## 1.2.0

### Added

- 真正的 Windows 桌面客户端入口 `desktop.py`，使用 WebView2 在独立窗口内渲染现有 UI。
- 通知区域托盘、关闭到托盘、单实例唤醒、窗口位置/尺寸记忆和 WebView2 Runtime 缺失提示。
- 安装版随包提供桌面壳运行时依赖和第三方许可证文件。

### Changed

- `LocalOpsWindows.exe` 普通启动不再调用外部浏览器；`--browser` 仅作为显式调试入口。
- 启动台布局针对 980–1400px 桌面窗口收敛，窄窗口自动隐藏侧栏信息组件。
- Windows 监听端口禁止地址复用，避免多个实例同时占用同一端口并交叉处理请求。

## 1.1.0

### Added

- 用户态 `LocalOpsWindows-Setup.exe` 安装器，包含开始菜单/桌面快捷方式、HKCU 开机自启、卸载注册表项和静默后台模式。
- PyInstaller 构建链路和 GitHub Actions Release 发布任务。
- 设置中心的 GitHub Release 检查、下载、SHA-256 校验和更新交接。
- 冻结版专用 `LocalOpsWindowsAnchor.exe`，不再依赖目标机器安装 Python。

### Security

- 更新资产仅接受个人仓库的 HTTPS Release URL，并限制安装包大小后再校验 SHA-256。
- 更新安装由独立安装器等待旧进程退出后替换文件，不覆盖 `%APPDATA%` 配置和日志。

## 1.0.0

### Added

- Windows 10/11 专用启动器 `start.bat` 和 `start_console_win.vbs`。
- 基于 IP Helper API 的 TCP 监听端口扫描。
- 基于 Toolhelp 与原生 Win32 API 的进程、创建时间、内存和命令行快照。
- Windows 受控进程锚点、run token、PPID 成员校验和精确进程树停止。
- 启停并发下持续发现同用户新后代，避免快速停止时留下孤儿进程。
- Windows 真实生命周期测试、隐私扫描和可重复 zip 发行检查。
- `NOTICE.md` 与 `PLAN.md`，记录上游来源、二开范围和发布流程。

### Changed

- 产品名改为“本地运维台 Windows / Local Ops Windows”。
- 默认数据目录改为 `%APPDATA%\LocalOpsWindows`，日志目录改为 `%LOCALAPPDATA%\LocalOpsWindows\logs`。
- API 平台字段固定为 `windows`，并公开 `capabilities` 供前端隐藏不可用操作。
- 停止受控应用改用 Win32 `TerminateProcess`，不再依赖 `taskkill`、CIM 或本地化命令输出。
- 发行包名改为 `local-ops-windows-<version>.zip`。

### Security

- Windows 版禁止认领外部进程和结束任意外部进程。
- 端口、PID、进程名、cwd 或 PPID 均不能单独作为进程所有权证明。
- 进程身份不完整或快照失败时拒绝破坏性操作。
- 发行检查拒绝个人绝对路径、密钥、令牌、运行数据和敏感文件。

### Removed

- macOS `.app`、`start.command`、Info.plist、ICNS 和 `iconutil` 发行链路。
- macOS CI 和 macOS 安装/签名/公证发布要求。
- 展示旧品牌、macOS 快捷键和 Unix 路径的历史截图。

## Upstream Base

本分支基于 `laogou717/local-ops` 和上游 PR #2 的 Windows 工作继续开发，并参考 PR #3、PR #4。完整归属见 `NOTICE.md`。
