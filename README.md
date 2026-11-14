# PixelView

Windows x64 图片查看器，面向像素画和日常看图。滚轮缩放图片，窗口位置和大小保持稳定。

[下载安装版或免安装版](https://github.com/Soulk000/PixelView/releases/latest) · [MIT 协议](LICENSE)

## 下载与升级

推荐使用 `PixelView-1.2.2-Setup.exe`。安装版包含所需运行时，安装到当前账户的 `%LOCALAPPDATA%\Programs\PixelView`，无需管理员权限。关闭旧版窗口后运行新版安装程序即可升级。

也提供 `PixelView-1.2.2-Windows-x64.zip`，解压后运行 `PixelView.exe`。

首次设置默认看图器，打开「设置 → 默认打开方式」，勾选图片格式，再到 Windows 默认应用页面确认。安装版以后升级会更新同一位置的程序，已确认的关联可以继续使用。免安装版设置默认关联时也会在上述固定目录保留程序副本，升级免安装版后需再点击一次设置默认打开方式，以更新该副本。

设置保存在 `%LOCALAPPDATA%\PixelView\settings.json`。安装包目前未签名。

## 功能

- 最近邻像素显示，支持整数倍率放大、鼠标指向位置缩放、拖动平移。
- 「固定视图」在翻图时保留当前缩放倍率和拖动位置。
- 「固定窗口」锁定窗口的位置与大小。
- 画布两侧翻图箭头靠近边缘时出现，停留后自动淡出。
- 同目录自然排序翻图，支持打开文件、拖放和剪贴板粘贴。
- GIF、WebP、APNG 动图播放，以及 TIFF 等多页图片翻页。
- 全屏、置顶、无边框、透明棋盘格、背景颜色及工具栏显示设置。
- 当前账户的默认图片打开方式注册及格式选择。

## 格式

解码主要使用 Magick.NET / ImageMagick，部分格式通过 Windows 图像组件读取。已验证样本包括 PNG、JPEG、BMP、GIF、WebP、APNG、TIFF、ICO、SVG、HEIC、AVIF、JPEG XL、JPEG 2000、PSD、DDS、TGA、OpenEXR、QOI 和 Nikon NEF。

具体支持取决于编码和文件变体。SVG 会栅格化显示，分层图片显示合成预览。PDF、PostScript 和视频不在本版查看范围内。单帧上限约 1.1 亿像素，超过内存预算的 APNG 会尝试静态预览。

## 快捷键

| 快捷键 | 操作 |
| --- | --- |
| Ctrl+O / Ctrl+V | 打开 / 粘贴图片 |
| ← / → | 上一张 / 下一张 |
| 滚轮 / + / - | 缩放 |
| 左键拖动 | 平移图片 |
| 0 / 1 | 适应画布 / 实际像素 |
| Ctrl+K | 固定视图 |
| Ctrl+L | 固定窗口 |
| Space | 暂停 / 继续动图 |
| PgUp / PgDn | 上一帧或页 / 下一帧或页 |
| F11 / Esc | 切换全屏 / 退出全屏 |
| Ctrl+T | 显示 / 隐藏工具栏 |

在「设置 → 透明背景」（或右击画布）中，可选择棋盘格、白色、黑色、灰色或跟随画布。切换立即生效并自动记住，只改变查看时的底色，原图透明通道保持不变。

窗口横向和纵向都可继续缩小，最小保留 1 个物理像素的画布（外部仍有系统边框与标题栏）。空间不足时工具栏和状态栏自动收起，放大后恢复；仍可右击画布打开设置，或使用快捷键。

完整操作说明见 [安装版说明](docs/使用说明-安装版.txt) 和 [免安装版说明](docs/使用说明-免安装版.txt)。

## 从源码构建

需要 Windows x64 和 .NET 10 SDK。NuGet 会还原 Magick.NET 依赖。

```powershell
dotnet run --project src/PixelView/PixelView.csproj
```

生成自带运行时的单文件程序和免安装 ZIP

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
```

生成安装版还需要 NSIS 3（包含 MUI2）

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build.ps1 -Installer -MakeNsisPath 'C:\Program Files (x86)\NSIS\makensis.exe'
```

构建结果位于 `artifacts/`。也可将 `makensis.exe` 加入 PATH 后省略 `-MakeNsisPath`。脚本默认使用 .NET 10.0.14 运行时，可用 `-RuntimeVersion` 指定其他兼容补丁版本。编译器和打包器的版本可能影响二进制文件哈希。

## 许可证

PixelView 自有源码、图标、文档和构建脚本采用 [MIT 协议](LICENSE)。Magick.NET、ImageMagick、.NET 及其依赖保留各自的许可证与版权声明，详见 [第三方组件说明](THIRD_PARTY_NOTICES.md) 和 `licenses/`。
