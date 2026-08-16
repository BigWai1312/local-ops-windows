# 本地运维台 Windows
`Local Ops Windows` 是一个面向 Windows 10/11 的本地服务监控与任务启动台。它使用 Python 标准库提供只绑定 `127.0.0.1` 的后端，前端为无构建、无 CDN 的原生 HTML/CSS/JavaScript。

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

完整范围见 [`PLAN.md`](PLAN.md) 和 [`NOTICE.md`](NOTICE.md)。

## 功能

- 查看本机监听端口、进程、CPU、内存、运行时长和启动来源。
- 保存常用服务或批处理任务，集中启动、停止、重启、查看日志和诊断。
- 从项目目录识别 Node、Python、Go、Rust、Docker Compose 和静态站点命令。
- 运行前检查工作目录、脚本、运行时和端口占用。
- 使用随机 token 和受控锚点识别本程序启动的进程树。
- Windows 上不认领或结束外部进程，不按端口杀进程。

## 系统要求

- Windows 10/11 x64。
- Python 3.12 或更高版本。
- Windows PowerShell 5.1 或 PowerShell 7。
- Chrome、Edge、Firefox 等支持 ES Modules 的现代浏览器。

运行时不需要安装第三方 Python 包。Pillow 等只用于开发期资源生成，不随程序运行。

## 启动

### 隐藏窗口启动

双击：

```text
start_console_win.vbs
```

### 调试启动

双击 `start.bat`，或在 PowerShell 中运行：

```powershell
py -3 server.py
```

可选参数：

```powershell
py -3 server.py --no-browser
py -3 server.py --preferred-port 9603
```

程序只绑定 `127.0.0.1`，默认从端口 `9600` 开始，被占用时依次尝试到 `9609`。

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
py -3 server.py
```

覆盖值必须是非空绝对路径，并指向专用子目录；不要使用磁盘根目录、用户主目录或项目根目录。

## 安全边界

- 后端只监听回环地址。
- Windows 版不开放外部进程认领和任意进程结束接口。
- 只有带当前随机 token、匹配受控锚点和进程树的应用可以停止或重启。
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
```

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
