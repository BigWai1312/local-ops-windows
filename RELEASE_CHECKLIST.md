# 本地运维台 Windows 发布核对表

## 身份与来源

- [ ] `VERSION`、标签、发行包名和发行说明一致。
- [ ] README、NOTICE、LICENSE 保留上游来源和 MIT License。
- [ ] 提交作者/提交者信息真实且不泄露私人邮箱。
- [ ] PR 描述列出 PR #2/#3/#4 的采用与差异。

## 自动检查

- [ ] `python tools/check_privacy.py` 通过。
- [ ] `python tools/check_project.py` 通过。
- [ ] `python -m unittest tests.test_windows -v` 通过。
- [ ] JavaScript 语法和 Node 行为测试通过。
- [ ] `python tools/build_release.py --dist dist` 通过。
- [ ] `python tools/build_release.py --dist dist --verify-only` 通过。
- [ ] `python tools/build_installer.py --output dist` 生成安装包和 SHA-256。
- [ ] GitHub Actions `windows-latest` 全部通过。
- [ ] C# 客户端测试与更新完整性、资源回退测试通过。
- [ ] 用 `--test-identity` 构建 Inno 安装器，`tools/verify_native_installer.py` 验证真实隔离安装、载荷摘要、解压代码隐私和冻结后台生命周期。
- [ ] 正式安装包载荷和维护程序的二进制/解压代码隐私检查通过；独立 Windows 环境验证正式身份的最终安装包，不能由测试身份包结果替代。
- [ ] 新标准向导的真实编译、首次安装、安装失败恢复和同目录更新已验证；历史自制安装器的通过记录不代替本项。

## Windows 生命周期

- [ ] 普通用户权限下可启动，不要求管理员权限。
- [ ] 后台仅监听 `127.0.0.1`，优先 9600-9609，全部不可用时安全回退至系统空闲端口；客户端从端点文件发现后仍核验数据目录与 PID。
- [ ] HTTP fixture 可启动、被识别、记录日志并被完整停止。
- [ ] 任务退出码 0/130/其他值分别显示成功/取消/失败。
- [ ] 外部进程认领和任意进程结束入口不可用。
- [ ] 同端口外部进程不会被认作受控进程或被结束。
- [ ] 进程快照失败时破坏性操作 fail-closed。
- [ ] 安装向导可选择中文、空格和非 C 盘路径，更新沿用已安装位置。
- [ ] 欢迎、许可、目录、附加选项、准备安装、进度和完成页面可由用户操作；桌面快捷方式和登录自启首次安装默认不勾选。
- [ ] 配置、偏好、图标、日志、资源缓存、更新下载和运行临时文件均位于所选目录；卸载保留数据、日志及未知文件。
- [ ] 安装向导没有旧版迁移选项；新空目录首次启动项目为空，不复制历史 AppData 的配置、偏好和日志，同目录更新保留数据。
- [ ] HKCU Run 使用 `--background`，登录后不弹浏览器或终端窗口。
- [ ] 安装版在未安装 Python 的环境可启动应用并运行受控锚点。
- [ ] 第二次启动唤醒现有窗口；启用相应设置后最小化到托盘，托盘“显示主窗口/退出”可恢复或终止客户端。
- [ ] C# 自包含安装版不依赖 WebView2、系统 Python 或已安装的 .NET Runtime。

## 数据与隐私

- [ ] 发行包不包含 `data/`、日志、缓存、数据库、备份或本地配置。
- [ ] 不包含个人绝对路径、用户名、邮箱、机器名或真实命令。
- [ ] 不包含 `.env`、Cookie、token、API key、SSH key 或其他凭据。
- [ ] README、模板、截图和发行说明已脱敏。
- [ ] 解压后的最终 zip 再次通过隐私扫描和冒烟测试。

## 发行内容

- [ ] 只包含 Windows 启动器；不包含 `.app`、`start.command`、Info.plist 或 ICNS。
- [ ] `THIRD_PARTY_NOTICES.md`、`ASSET_PROVENANCE.md` 和许可证原文随包提供。
- [ ] 发行 zip 的 SHA-256 和字节数已记录。
- [ ] `LocalOpsWindows-Setup.exe` 与 `.sha256` 均已附加到 GitHub Release。
- [ ] 源码 ZIP 可重复构建验证通过，两次构建字节一致；EXE 按各次实际构建记录 SHA-256。
- [ ] `LocalOpsWindows.Update.json`、`LocalOpsWindows.Resources.json` 与安装包摘要及版本一致。

## GitHub

- [ ] 发布到正确的个人仓库和独立分支。
- [ ] 未向上游 `laogou717/local-ops` 的 `origin` 推送。
- [ ] 草稿 PR 包含变更、测试、隐私、许可证、已知限制和回退说明。
- [ ] 最终提交文件列表已人工复核。
