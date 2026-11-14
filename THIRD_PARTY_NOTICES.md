# 第三方软件与素材声明

项目有权许可的自有代码和文档采用根目录 `LICENSE` 中的 MIT License。MIT License 不会自动改变下列第三方素材、品牌素材或 AI 生成素材的许可状态；对外分发前必须核对来源、版本、完整许可和再分发范围，并由发布负责人确认。

素材级来源、SHA-256、修改记录与发布状态见 `ASSET_PROVENANCE.md`。两份文件必须同步维护：本文件说明适用权利和上游声明，素材台账负责逐文件追溯与发布门禁。

## Lucide Icons

- 位置：`static/icons/*.svg` 与由它们生成的 `static/icons.js`
- 版本：`lucide-static` 0.544.0（依据 SVG 文件头）
- 项目：<https://github.com/lucide-icons/lucide>
- 许可：ISC
- 随包许可原文：`licenses/Lucide-LICENSE.txt`（含部分 Feather 图标适用的 MIT 条款）

Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part
of Feather (MIT). All other copyright (c) 2022, Lucide Contributors.

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY
AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR
OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.

## Geist Mono

- 位置：`static/fonts/GeistMono-Variable.woff2`
- 项目：<https://github.com/vercel/geist-font>
- 许可：SIL Open Font License 1.1
- 版权：Copyright (c) 2023 Vercel, in collaboration with basement.studio
- 随包许可原文：`licenses/Geist-OFL-1.1.txt`

OFL 1.1 允许字体与软件一同捆绑和再分发，前提是每份副本包含版权声明与 OFL 许可文本，且不将字体文件单独出售。上游原文见：<https://github.com/vercel/geist-font/blob/main/LICENSE.txt>。

## 项目图像

- `static/assets/console-app-icon.png`、`brand-mark.png`、`favicon-32.png`、`favicon.ico` 与 `apple-touch-icon.png` 来自同一套品牌方向；派生图标由 `tools/gen_brand_assets.py` 生成。
- 上述品牌素材按上游记录由其维护者于 2026-07-23 使用 OpenAI Image Generation `image_gen` 工具定向生成；Windows 二开原样继承，未声称本机重新生成。具体模型版本未知。

2026-11-12 的固定来源、C# 图标生成记录和官方条款复核见 `licenses/ASSET_REVIEW.md`。该记录区分自动来源审核与人工法律判断，保留模型未知、输出非独占等限制；素材改变时必须重新登记和复核。

## 开发期工具

`tools/gen_brand_assets.py` 使用 `requirements-dev.txt` 精确锁定的 Pillow 生成 PNG/ICO；`tools/gen_icons.py` 由 vendored Lucide SVG 重新生成 `static/icons.js`。这些工具只用于重新生成已入库资源，不随本地运维台 Windows 运行，也不是运行时依赖。更新版本时必须重新核对各自上游许可和来源记录。

## C# 安装版运行时

2.1.0 使用 .NET 10 WPF，标准库后台与锚点由 PyInstaller 冻结。构建器从实际解析的 .NET 运行时 NuGet 包复制许可证与第三方声明，并从构建用 Python、PyInstaller 分发复制许可到安装目录的 `licenses/runtime/`。版本以随包 `LocalOpsWindows.Client.runtimeconfig.json` 为准。C# 安装包不包含 pywebview、pystray 或 WebView2。

标准安装向导改用 Inno Setup，维护程序仍由 PyInstaller 冻结，不再打包 Tkinter / Tcl/Tk。安装流程参考 VS Code 的公开 `build/win32/code.iss`，本项目使用自行编写的安装脚本和常用中文消息，未复制其产品标识或翻译者个人信息。

- 项目：<https://jrsoftware.org/isinfo.php>
- 当前构建器：Inno Setup 7.1.1；许可原文随构建工具的 `license.txt` 提供。
- 源码与许可：<https://github.com/jrsoftware/issrc>
- 许可：Inno Setup License；Copyright (C) 1997-2026 Jordan Russell，Portions Copyright (C) 2000-2026 Martijn Laan。
- 构建使用原版编译器并保留其生成安装器中的版权声明和网站信息，不冒称安装引擎作者。升级构建器版本时需重新核对所用版本的许可。

## 历史 WebView2 桌面壳

2.0.2 及历史 `desktop.py` 宿主使用以下 Python 运行时组件。保留来源与许可供历史版本追溯；它们不是 2.1.0 C# 客户端的依赖：

- `pywebview 6.2.1`：BSD 3-Clause，原文见 `licenses/pywebview-LICENSE.txt`。
- `pystray 0.19.5`：LGPL-3.0-or-later，原文见 `licenses/pystray-COPYING.txt` 和 `licenses/pystray-LGPL-3.0.txt`。
- `pythonnet 3.1.0`、`clr-loader 0.3.1`：MIT，原文见对应许可证文件。
- `bottle 0.13.4`、`proxy-tools 0.1.0`、`six`：MIT，原文见对应许可证文件。
- `Pillow`：Pillow 许可，原文见 `licenses/Pillow-LICENSE.txt`。

WebView2 的浏览器运行时由 Microsoft Windows 组件提供，安装版不把运行时二进制重新分发到仓库。首次启动会检查系统 WebView2 Runtime；缺失时只显示安装提示，不自动打开外部浏览器。
