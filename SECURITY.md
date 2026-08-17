# Security Policy
本地运维台 Windows 会以当前 Windows 用户权限执行用户保存的命令。命令执行、进程所有权、写接口授权、路径处理和配置完整性均属于高影响安全边界。

## 支持范围

- Windows 10/11 x64。
- Python 3.12+。
- 仅限本机回环地址，不支持公网、反向代理、端口映射或多用户远程管理。

## 进程控制边界

- 不允许通过端口、裸 PID、进程名、cwd 或 PPID 单独结束进程。
- Windows 版不开放外部进程认领和任意进程结束能力。
- 只有本程序启动、带当前随机 token、并通过受控进程树校验的应用可以停止或重启。
- 身份不完整、进程快照读取失败或状态发生竞态时，操作必须 fail-closed。
- 测试和诊断不得结束用户已有进程。

## HTTP 边界

- 只绑定 `127.0.0.1`。
- 写接口必须校验 Host、Origin、Sec-Fetch、会话 Cookie 和 Content-Type。
- 不允许通过 CORS、DNS rebinding 或简单表单请求绕过写接口保护。

## 敏感信息

以下内容不得提交到 GitHub、发行包或公开问题：

- `%APPDATA%\LocalOpsWindows\config.json{,.bak}`；
- `%LOCALAPPDATA%\LocalOpsWindows\logs\`；
- 完整命令、真实工作目录、用户名、机器名和邮箱；
- token、Cookie、API key、SSH key、`.env` 和账号凭据；
- 未脱敏的截图、崩溃转储和调试日志。

示例路径统一使用 `C:\Users\example\project`。

## 报告漏洞

优先使用目标 GitHub 仓库的 Private Vulnerability Reporting。若私密入口不可用，不要在公开 Issue、Discussion 或 Pull Request 中披露复现步骤、日志、配置或路径。
