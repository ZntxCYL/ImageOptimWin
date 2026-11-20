# ImageOptimWin

基于 **C# / WPF / .NET 10** 的非官方 Windows 图片优化工具，交互风格参考 [macOS ImageOptim](https://github.com/ImageOptim/ImageOptim)。支持中文界面、深浅色自动切换、拖入文件与文件夹、逐文件压缩状态和进度、结果统计、重复导入及会话内恢复。

**项目状态：v0.9.0 源码原型。** 已集成自动打包和 GitHub Releases 更新逻辑，但完整 Windows GUI、安装、更新与回滚仍需实机测试。仓库源码 **不是** 已通过验收的 Windows 正式版本。该项目不是 ImageOptim 官方移植版。

## 功能与压缩引擎

| 图片 | 构建时自动打包的引擎 | 默认行为 |
| --- | --- | --- |
| PNG / APNG | OxiPNG 10.2.1、pngquant 2.18.0 | OxiPNG 无损；pngquant 有损模式可选 |
| JPG / JPEG | JPEGOptim 1.5.6、MozJPEG 4.1.5 JPEGTran | 默认无损；可选择有损优化 |
| GIF | Gifsicle 1.97 | 无损优化 |
| SVG | SVGO 3.3.2 + 便携 Node.js 22.16.0 | 多轮 SVG 结构优化 |

构建脚本会自动取得、校验和随包分发 **本项目使用的引擎**；用户运行已构建的离线发布 ZIP 时，不需要额外安装压缩器或 .NET。它没有包含 macOS 原版的全部压缩算法，输出大小也不保证与原版一致。

## Windows 构建（推荐）

构建电脑需要 Windows x64、.NET 10 SDK、网络连接。

1. 双击 `build-windows.cmd`，或在 PowerShell 里运行：

   ```powershell
   .\publish.ps1 -Runtime win-x64
   ```

2. 产物：`dist/ImageOptimWin-win-x64-Bundled.zip`。解压后运行 `ImageOptim.Win.exe`。

3. 也可以使用仓库的 **Actions → Windows bundled build** 工作流，下载其构建产物。CI 产物不等于已通过实机 GUI 验收的正式 Release。

压缩会在文件变小时替换原文件；首次测试请用**图片副本**。会话内恢复并非永久备份。

## 其他构建方式

`build-linux.sh` 可在临时 Debian 构建机上交叉编译 Windows x64 包：

```bash
bash build-linux.sh
```

Debian **不是更新服务器**，也不是发布的必要常驻基础设施。构建结束后可以销毁；更新文件由 GitHub Releases 长期托管。

## 自动更新与 GitHub Releases

v0.9 包含独立 `ImageOptim.Updater.exe`、清单签名验证、下载进度和启动失败回滚尝试。更新主 EXE 时需要关闭应用并重启，不支持运行时无缝替换 DLL。

- `src/ImageOptim.Win/update-config.json` 初始为空：**未配置仓库/公钥时自动更新不可用**。
- 正式发布前，用 `scripts/configure-updates.py` 写入公开仓库和 RSA **公钥**，重新构建。
- RSA **私钥** 必须保存在源码仓库之外，并做好长期安全备份；不要提交 GitHub。
- 发布需要经过 Windows 实机启动、压缩、更新与回滚测试；不能仅凭源码检查宣称功能已通过。
- 详细操作：[更新与签名指南](UPDATE_GUIDE_中文.md)、[首次发布说明](GH_首次发布_中文.md)。

## 开发验证

运行已有源码回归检查：

```bash
python3 scripts/check-source-config.py
python3 scripts/check-v08-regressions.py
python3 scripts/check-v081-build.py
python3 scripts/check-v09-updater.py
bash -n build-linux.sh
```

这些检查防止历史问题回归，**不能代替 .NET 实际编译和 Windows 运行测试**。

## 工程目录

```text
src/ImageOptim.Win/       WPF 主程序、中文界面、压缩队列及主题
src/ImageOptim.Updater/   独立 Windows 更新器
scripts/                  引擎打包、版本签名、发布与回归检查
.github/workflows/        Windows CI 构建
build-windows.cmd         Windows 一键构建入口
publish.ps1               Windows 发布脚本
build-linux.sh            可选：Linux 交叉编译入口
CHANGELOG.md              合并后的唯一版本更新记录
```

## 文档与许可

- [CHANGELOG.md](CHANGELOG.md)：各版本修改记录
- [UPDATE_GUIDE_中文.md](UPDATE_GUIDE_中文.md)：自动更新、签名和部署
- [GH_首次发布_中文.md](GH_首次发布_中文.md)：首次发布检查清单
- [THIRD_PARTY.md](THIRD_PARTY.md)：压缩引擎来源、许可证和再分发注意事项

WPF 前端独立编写；压缩工具依各自许可证分发。用户提供的 ImageOptim 应用图标和状态图片可能另受素材版权约束，公开分发前应确认授权。软件不保证处理所有格式或保留所有元数据，发布与使用前请保留原始文件备份。
