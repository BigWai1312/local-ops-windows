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
- [ ] 使用独立 `--test-identity` 构建测试包，`tools/verify_native_installer.py` 验证清单、解压代码隐私、标准安装、覆盖保留数据及卸载边界；正式 payload 再次通过隐私扫描。

## Windows 生命周期

- [ ] 普通用户权限下可启动，不要求管理员权限。
- [ ] 桌面客户端仅监听 `127.0.0.1:9600-9609` 或备用 `11000-11009` 中的一个端口，未暴露到局域网或公网。
- [ ] HTTP fixture 可启动、被识别、记录日志并被完整停止。
- [ ] 任务退出码 0/130/其他值分别显示成功/取消/失败。
- [ ] 外部进程认领和任意进程结束入口不可用。
- [ ] 同端口外部进程不会被认作受控进程或被结束。
- [ ] 进程快照失败时破坏性操作 fail-closed。
- [ ] 安装、覆盖更新和卸载只操作用户选定的安装目录；覆盖更新保留 `data/logs/cache`，卸载保留 `data/logs` 和未知用户文件。
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
