# ImageOptimWin

基于 **C# / WPF / .NET 10** 的非官方 Windows 图片优化工具，交互风格参考 [macOS ImageOptim](https://github.com/ImageOptim/ImageOptim)。支持中文界面、深浅色自动切换、拖入文件与文件夹、逐文件压缩状态和进度、结果统计、重复导入及会话内恢复。

**项目状态：v0.9.2 正式版源码（更新器回滚仍需实机补充验证）。** 已集成自动打包和 GitHub Releases 更新逻辑，但完整 Windows GUI、安装、更新与回滚仍需实机测试。仓库源码 **不是** 已通过验收的 Windows 正式版本。该项目不是 ImageOptim 官方移植版。

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

- 更新仓库已配置为 **`ZntxCYL/ImageOptimWin`**，客户端可信 RSA 公钥位于 `src/ImageOptim.Win/update-config.json`。
- **普通推送**：`Windows bundled build` 仅编译和上传 CI 测试包。
- **手动执行** `Signed Windows release`：仅构建并验证签名，不创建 Release。
- **推送版本标签** `v0.9.0` / `v0.9.1`：`Signed Windows release` 自动构建、验证、签名并发布正式 GitHub Release（版本必须与两个 `.csproj` 一致）。
- 签名依赖仓库 Actions Secret `IMAGEOPTIM_UPDATE_PRIVATE_KEY`；**私钥只保存于源码目录外与 GitHub Secret，不能提交 Git**，并须长期备份。尚未完成 Secret 配置时签名阶段会明确失败。
- 发布前仍须在 Windows 实机测试启动、压缩和更新回滚；CI 通过不等于更新器已完成全流程验证。
- 详细操作：[更新、签名及首次发布指南](UPDATE_GUIDE_中文.md)。

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
- [THIRD_PARTY.md](THIRD_PARTY.md)：压缩引擎来源、许可证和再分发注意事项

WPF 前端独立编写；压缩工具依各自许可证分发。用户提供的 ImageOptim 应用图标和状态图片可能另受素材版权约束，公开分发前应确认授权。软件不保证处理所有格式或保留所有元数据，发布与使用前请保留原始文件备份。
