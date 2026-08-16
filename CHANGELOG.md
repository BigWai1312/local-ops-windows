# Changelog

## Unreleased

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
