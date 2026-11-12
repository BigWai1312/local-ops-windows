# Local Ops Windows
本仓库是 `laogou717/local-ops` 的 Windows-only 二开版本。默认使用简体中文回复和文档，命令、代码标识符、日志和错误信息保持原文。

## 产品边界

- 产品名：本地运维台 Windows / Local Ops Windows。
- 目标平台：Windows 10/11 x64。
- Python 3.12+，运行时只使用标准库。
- 后端仅绑定 `127.0.0.1`，端口从 9600 到 9609 递增尝试。
- 配置目录：`%APPDATA%\LocalOpsWindows`。
- 日志目录：`%LOCALAPPDATA%\LocalOpsWindows\logs`。
- 常规启停只控制本程序启动且 token 校验通过的进程树，不认领外部进程。按用户要求，C# 客户端提供独立的端口冲突手动强停入口，必须显示真实目标并二次确认。

## 关键文件

- `server.py`：HTTP/API、配置、扫描、生命周期和静态文件服务。
- `tools/win_anchor.py`：受控应用锚点。
- `static/`：原生前端，无构建、无 CDN。
- `start.bat`：调试启动入口。
- `start_console_win.vbs`：隐藏窗口启动入口。
- `tests/test_windows.py`：Windows 解析和生命周期测试。
- `tools/check_privacy.py`：源码隐私扫描。
- `tools/build_release.py`：发行包构建和泄露检查。

## 安全要求

- 端口、裸 PID、进程名、cwd、PPID 或命令行不能单独证明所有权。
- 常规停止/重启操作先验证随机 token 和受控进程树。
- C# 手动强停必须重新读取唯一监听者，核验当前用户、会话、进程创建时间和可执行路径，并在同一句柄上核验与结束；拒绝系统关键进程、系统服务及运维台自身。
- 手动强停不递归结束外部进程树、不自动重复或重启。仅拒绝访问时经用户选择启动一次性 UAC helper，helper 必须再次展示并确认目标；不得把整个客户端或 HTTP 后台常驻提权。
- 外部 attach/kill API 在 Windows 上必须 fail-closed。
- 配置写入使用临时文件 + `os.replace`，保留上一份良好备份。
- 测试只操作自身创建的 fixture 进程和动态端口。
- 禁止提交配置、日志、token、Cookie、API key、真实命令、用户名、邮箱或个人绝对路径。

## 修改流程

1. 阅读 `PLAN.md`、`README.md`、`SECURITY.md` 和 `NOTICE.md`。
2. 优先复用 PR #2/#3/#4 已有 Windows 实现，不重复实现同类功能。
3. 小范围修改使用 `apply_patch`；不要改动无关文件。
4. 运行：

   ```powershell
   python -m unittest tests.test_windows -v
   python tools/check_privacy.py
   python tools/check_project.py
   ```

5. 发布前运行发行构建和二次隐私扫描。

## 二开与许可

- 保留 `LICENSE`、`NOTICE.md`、`THIRD_PARTY_NOTICES.md` 和 `ASSET_PROVENANCE.md`。
- 不得删除或伪造上游归属。
- README 和 PR 描述必须写清 Windows-only 二开范围、复用来源、测试和未完成项。
