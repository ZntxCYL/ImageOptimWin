# ImageOptimWin 自动更新与 GitHub Releases 发布指南

## 当前状态与架构

**GitHub 仓库：** `ZntxCYL/ImageOptimWin`。Debian 不是服务器，也不参与日常发布。GitHub Actions 的 Windows Runner 构建可离线运行的 EXE 和压缩引擎，Ubuntu Runner 负责 RSA-SHA256 签名、校验，并在正式版标签推送时上传 GitHub Releases。

- `Windows bundled build`：每次普通推送自动构建和运行压缩引擎冒烟测试；产物为 Actions Artifact，不是用户自动更新源。
- `Signed Windows release`：手动运行只做构建、签名、复核，不创建 Release；推送合法的 `vMAJOR.MINOR.PATCH` 标签，且与两个 .csproj 版本一致时，自动发布正式 Release。
- 客户端从 GitHub Releases 的 latest 正式版获取 `update-manifest.json`、`update-manifest.sig`、`ImageOptimWin-win-x64-Bundled.zip`，用内置公钥验证 RSA 签名，再对 ZIP 做 SHA-256 和大小校验。
- 完成下载后，独立 `ImageOptim.Updater.exe` 在主程序退出后替换目录、启动新版；若启动健康检查失败会尝试回滚。**更新 EXE 必须重启应用，不是运行时热替换。**

## 一次性的签名密钥与 Secret

可信仓库及 RSA 公钥已经写进 `src/ImageOptim.Win/update-config.json`，可以公开提交。对应 RSA 3072 位私钥保存在**开发者自己长期持有的 Mac**（项目目录之外）：

```text
~/.config/imageoptimwin/signing/private.pem
~/.config/imageoptimwin/signing/public.pem
```

**务必将 private.pem 离线加密备份。** GitHub Secrets 不支持取回密钥；如果本地文件丢失，旧版程序将无法验证由新密钥签名的更新，不要随意重新生成或覆盖。

使用拥有此密钥的 Mac，执行一次：

```bash
gh secret set IMAGEOPTIM_UPDATE_PRIVATE_KEY \
  --repo ZntxCYL/ImageOptimWin \
  < ~/.config/imageoptimwin/signing/private.pem
```

这会把私钥经 GitHub CLI 写入**仓库 Actions Secret**，不用在终端输入或打印私钥。使用 `gh secret list -R ZntxCYL/ImageOptimWin` 确认 Secret 名称存在（不会显示值）。

绝不要把私钥提交到 Git、复制到 `src/`、作为 Release 附件或发送到聊天；源代码和程序中仅包含公钥。

## CI 签名流程的无发布测试

在仓库 Actions 里选择 **Signed Windows release** → **Run workflow** → **main**。这一步会：

1. 运行历史回归检查和 WPF/.NET 编译。
2. 下载校验所有压缩引擎，生成包含 updater.exe 的 Windows x64 ZIP。
3. 从 Actions Secret 临时加载私钥，制作 RSA-SHA256 签名清单。
4. 用公钥、版本号、ZIP 大小与 SHA-256 验证签名、更新包和内置的仓库配置。
5. 上传测试用 `release-bundle` 和 `signed-release-manifest` Artifacts。

**不会创建 GitHub Release，也不会改变用户当前的 latest 版本。** 签名步骤若报告缺少 `IMAGEOPTIM_UPDATE_PRIVATE_KEY`，说明 Secret 尚未设置。

## 首次正式发布

先下载 Actions 构建包，在真实 Windows 10/11 x64 上验证启动、拖拽压缩、状态图标、窗口和主题。更新器安装与回滚也必须另行验证；GitHub Runner 编译/CLI 冒烟测试不能证明图形界面或更新器所有场景正常。

确认 `main` 对应两个 .csproj 的版本均是 `0.9.0`，且公钥已正确打包后，在有 Git 推送权限的电脑执行：

```bash
git checkout main
git pull --ff-only
git tag v0.9.0
git push origin v0.9.0
```

标签推送会启动 `Signed Windows release`，在构建、引擎冒烟、签名与重新验证均通过后，创建正式 Release，固定上传：

```text
ImageOptimWin-win-x64-Bundled.zip
update-manifest.json
update-manifest.sig
```

工作流使用 GitHub Actions 的临时 `GITHUB_TOKEN`（发布任务限定 `contents: write`），拒绝覆盖同名 Release，并要求标签已存在。无需额外 GitHub Personal Access Token，也无需 Debian。

**重要：** 正式发布不应仅依据静态检查；本项目原版的 Windows 自动更新与回滚尚未完成实机测试。在验证前请勿推送正式版本标签。

## 发布下一个自动更新版本

例如 0.9.1：

1. 把 `src/ImageOptim.Win/ImageOptim.Win.csproj` 与 `src/ImageOptim.Updater/ImageOptim.Updater.csproj` 的 Version 和 FileVersion 升至 0.9.1。
2. 继续沿用原有 RSA 公钥/私钥和同一个公开仓库，不允许自行轮换信任根。
3. 提交、推送 main，先让 `Windows bundled build` 成功并做好 Windows 实机检查。
4. 创建并推送 `v0.9.1` 标签。GitHub Actions 自动签名并发布。
5. 已经安装带该公钥的旧版客户端可通过“检查更新”或启动时检查获取新正式版。

## 安全与限制

- 客户端使用 RSA-SHA256 签名清单及 ZIP SHA-256；这是**应用内更新可信性验证**，不等于 Microsoft Authenticode 代码签名，不能保证消除 SmartScreen 提示。
- 只检查**公开仓库正式 Release**，不读取未签名的 Actions Artifacts、草稿、预发布版本。
- 更新安装位置必须可写，建议 Windows 用户自己的应用目录；`Program Files`、多实例、文件被锁住、跨磁盘和杀毒软件可能导致安装或回滚失败。
- 当前使用整包更新而不是差分补丁；大约需要重新下载包含压缩引擎的 ZIP。
- 自动回滚基于新程序窗口在约 50 秒内的健康信号；不能覆盖所有运行时缺陷。保留自己的源图片备份和安装包。
- 如需临时 Debian 交叉编译，现有 `build-linux.sh` 仍可用；GitHub 托管更新不依赖 Debian。
