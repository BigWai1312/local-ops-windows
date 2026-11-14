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

安装器使用 Inno Setup 标准向导。候选安装包的本机验证记录见 `csharp/FEATURE_PARITY.md`，正式下载以完成发行验收后的 GitHub Release 为准。

`LocalOpsWindows-Setup.exe` 双击先打开安装向导，依次显示欢迎、许可协议、安装位置、附加选项、准备安装、安装进度和完成页面。可输入或浏览选择安装目录（支持中文和空格），点击“安装”后才开始写入。首次安装建议路径为：

```text
%LOCALAPPDATA%\Programs\LocalOpsWindows
```

可以改为 `D:\Apps\LocalOpsWindows` 等当前用户可写的独立目录；不接受磁盘根目录、系统目录根、目录链接或其他软件的非空目录。更新默认沿用已安装位置，不会恢复到 C 盘。

安装器会创建开始菜单快捷方式；“创建桌面快捷方式”和“登录 Windows 后启动”由用户选择，首次安装默认不勾选。它不要求管理员权限，也不会修改 Docker 项目、系统服务或已有端口。完成页面可选择是否启动客户端。卸载可从 Windows“已安装的应用”进入，或运行安装目录中的 `unins000.exe`。

公开安装包面向首次安装：没有旧版数据迁移选项，不会复制历史 AppData 或其他安装目录的配置、偏好和日志。安装到空目录后，首次启动的项目列表为空。现有客户端可手动添加项目和导出清单，尚无手动导入入口。标准安装版更新沿用已有目录和该目录中的数据；需要换盘时先卸载再安装，数据原件仍保留。卸载只删除已登记的程序文件和缓存，保留 `data`、`logs` 及其他用户文件。

安装完成后，双击快捷方式会打开客户端；开机自启使用隐藏窗口与通知区域模式。Inno Setup 管理程序文件、快捷方式、注册项和卸载记录，程序放在独立的 `versions/<版本>-<摘要>/` 目录。维护程序校验载荷后写入 `current.json`，失败则返回错误并禁止安装结束后启动。新版向导的安装失败恢复、旧版升级和卸载仍需真实 EXE 验证，不沿用旧自制安装器的回滚结论。

当前公开安装包未做商业代码签名，Windows SmartScreen 可能显示“未知发布者”。Release 同时提供 `.sha256` 文件；安装前可用 `Get-FileHash` 核对，但这不能替代正式代码签名。

在“设置 → 启动与窗口 → 点击关闭按钮时”选择“最小化到托盘”或“退出程序”，选择保存在安装目录的偏好文件中。默认关闭到托盘，右上角 × 和 Alt+F4 使用同一设置，后台继续运行；双击托盘图标可恢复窗口。选择退出程序或从托盘菜单退出时，会退出客户端并停止由它启动的管理后台；不会批量停止已托管项目。托盘不可用时正常退出，避免隐藏后无法找回窗口。

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

桌面客户端优先使用 `127.0.0.1:9600-9609`；此范围被 Windows 保留或全部不可用时，后台选择空闲端口，将实际端口写入数据目录的 `backend-endpoint.json`，客户端核验健康响应、数据目录与进程身份后连接。`static/` 网页版保留作为兼容及开发入口；`desktop.py` 是历史 WebView2 宿主，不进入 C# 安装版。

安装版支持 `LocalOpsWindows.Client.exe --background`，供开机自启使用；普通启动不加该参数会打开桌面客户端窗口。

## 数据与日志

默认位置：

| 内容 | 路径 |
| --- | --- |
| 配置、图标、客户端偏好 | `<安装目录>\data\` |
| 日志 | `<安装目录>\logs\` |
| 资源缓存、更新下载、运行临时文件 | `<安装目录>\cache\` |

这些目录不随版本目录切换而改变。源码后台默认使用项目目录下的 `data`、`logs` 和 `cache`；独立 C# 调试构建的偏好和缓存位于其输出目录。安装器首次启动的自解压临时目录仍由 Windows 的 `%TEMP%` 决定，正常退出后清理；选定目录后的日常后台和应用内更新临时文件使用安装目录的 `cache\temp`。Windows 快捷方式与卸载/自启注册项仍位于标准系统位置，不存储项目缓存。

源码后台可用环境变量覆盖（安装客户端固定使用其所选安装目录）：

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

构建发行包需要已安装的 Inno Setup 6.4+ 或 7 编译器（当前使用 7.1.1）。构建器按环境变量 `INNO_SETUP_COMPILER`、PATH、常规安装路径和编译器安装登记查找，也可使用 `--iscc` 明确指定现有 `ISCC.exe`；缺失时停止，不生成旧式安装器。

后台隔离验证使用独立产品身份，不安装正式产品、不创建真实桌面或开始菜单快捷方式，也不启动客户端窗口：

```powershell
python -m pip install -r requirements-build.txt
$testId = [guid]::NewGuid().ToString('N')
python tools/build_installer.py --output tmp/inno-test --test-identity $testId
python tools/verify_native_installer.py --setup tmp/inno-test/LocalOpsWindows-Setup.exe --test-identity $testId --payload build/csharp-installer/payload
```

隔离验证通过后再构建正式发行文件：

```powershell
python tools/build_release.py --dist dist
python tools/build_installer.py --output dist
python -c "from pathlib import Path; from tools.verify_installer import verify_archive_privacy; verify_archive_privacy(Path('build/csharp-installer/payload'), Path('build/csharp-installer/dist/LocalOpsWindows.Maintenance.exe'), embedded_payload=False)"
```

`tools/verify_installer.py` 的命令行保留用于历史 PyInstaller 安装包验证，不能用来执行 Inno Setup 安装包。标准向导验证使用上述 `verify_native_installer.py`；它检查真实测试安装包、中文和空格目录、首次启动项目为空且不读取模拟旧配置、可选自启、载荷摘要与代码隐私、冻结后台任务、重装和卸载。正式身份安装包不在用户当前系统自动运行，其最终二进制安装验收仍需独立 Windows 测试环境。

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
