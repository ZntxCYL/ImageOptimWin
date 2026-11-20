# ImageOptimWin 更新记录

本文件合并原有各版本的 CHANGES／版本说明，历史功能描述不代表已通过 Windows 实机验证。

## v0.9.1 — 更新提示及 Windows 图标兼容修复
- GitHub 仓库存在但仅有预发布版时，不再误报仓库不存在；手动检查显示准确提示，自动检查保持安静。
- WPF 窗口直接使用透明 PNG；EXE 使用重新制作的多尺寸 ICO，小图增加 Windows AND 透明遮罩。
- Windows CI 新增对已发布 EXE 嵌入图标透明度的实际检查，避免只校验源码文件。
- 本版仍作为预发布测试版，Windows 图形界面与更新器回滚需实机验收。

## v0.9.0 — GitHub Releases 自动更新
- 新增 GitHub Releases 版本检查、下载进度、RSA-SHA256 签名清单与文件 SHA-256／大小校验。
- 独立 `ImageOptim.Updater.exe` 负责关闭主程序、切换安装目录、重启及启动失败回滚。
- 增加更新配置、签名发布、Debian 构建集成和 `check-v09-updater.py`。
- 公共仓库、公钥需配置；私钥必须独立安全保管。Windows 实机更新/回滚尚待测试。

## v0.8.1 — Debian 与 SVGO 修复
- Bash 5.2 `set -u`：下载函数先声明 `filename` 再计算 `dest`，避免 unbound variable。
- SVGO 3.3.2 入口统一为 `node_modules/svgo/bin/svgo`，覆盖 Debian、Windows、C# 调用和冒烟测试。
- 增加 `check-v081-build.py` 离线回归检测。

## v0.8.0 — 界面与图标修复
- 透明化应用图标，清除绿色底色。
- 修复压缩完选择行时文字消失、再次拖入相同路径不能重新压缩。
- 状态图标添加透明显示边距，避免圆形边缘与旋转动画被裁切。
- 增加 `check-v08-regressions.py`。

## v0.7.2 — 构建配置持久化
- `Content Include="tools\\**\\*"` 保留 `ExcludeFromSingleFile="true"`，压缩引擎不嵌入单文件 EXE。
- 保留 `ThemeManager.cs` 的 `using System.IO;`，新增 `check-source-config.py`。

## v0.7.1 — 编译修复
- `ThemeManager.cs` 引入 `using System.IO;` 解决 `IOException` 引用问题。

## v0.7.0 — 原版状态图标
- 使用用户提供的 `wait/progress/ok/noopt/err` PNG 和 `@2x` 高清资源。
- 压缩时图标旋转，完成后自动切换状态；PNG 编译进 WPF 资源。

## v0.6.0 — 应用图标
- 将用户提供的 `ImageOptim.icns` 生成 Windows 多尺寸 ICO，应用于 EXE、窗口和任务栏。
- 对外分发需核实应用图标与状态图标的素材授权。

## v0.5.0 — 启动修复
- 切换主题时替换资源画刷，避免对冻结画刷直接赋值导致启动异常。
- 增加 `%LOCALAPPDATA%\\ImageOptimWin\\startup-error.log`。

## v0.4.0 — 中文化与外观
- 主窗口、菜单和偏好设置中文化；逐文件压缩阶段进度、完成状态与节省比例。
- 跟随 Windows 系统、浅色及深色三种主题。

使用和构建参见 [README.md](README.md)；更新与签名参见 [UPDATE_GUIDE_中文.md](UPDATE_GUIDE_中文.md)；第三方许可证参见 [THIRD_PARTY.md](THIRD_PARTY.md)。
