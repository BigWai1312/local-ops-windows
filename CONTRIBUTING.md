# 参与贡献

本地运维台 Windows 是 `laogou717/local-ops` 的 Windows-only 衍生版本。提交代码前请保留上游许可证和来源说明，不要把衍生版本描述为全部原创。

## 开发环境

- Windows 10/11 x64
- Python 3.12 或 3.13
- Node.js，用于 JavaScript 语法与行为测试
- 运行时无第三方 Python 依赖

仅重新生成图像资源时需要：

```powershell
python -m pip install -r requirements-dev.txt
```

## 修改原则

- 后端保持 Python 标准库实现，前端保持原生 ES Modules。
- HTTP 服务只绑定 `127.0.0.1`。
- 不按端口、进程名或裸 PID 结束进程。
- 只有通过 run token 和受控树校验的进程可以停止或重启。
- Windows 外部进程认领和任意结束能力保持关闭。
- 修改配置 schema 时提供幂等迁移和测试。
- 修改 `static/icons/*.svg` 后运行 `python tools/gen_icons.py`。

## 隐私与许可

不得提交配置、日志、真实命令、个人路径、用户名、邮箱、机器名、凭据、token、Cookie、数据库、缓存或未脱敏截图。

新增字体、图标、图片或其他素材时，同步更新 `ASSET_PROVENANCE.md` 和 `THIRD_PARTY_NOTICES.md`，并保留相应许可证。

## 提交前检查

```powershell
python tools/check_privacy.py
python tools/check_project.py
python tools/build_release.py --check-only
```

Pull Request 应说明改动范围、用户影响、安全边界、实际测试结果、隐私检查和回退方式。
