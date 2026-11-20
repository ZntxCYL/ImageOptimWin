# ImageOptimWin v0.9 — GitHub Releases 自动更新指南

> **Debian 只是临时构建机。** 更新托管在 GitHub Releases。先在长期可信机器备份 RSA 私钥，再在临时 Debian 上执行 `bash scripts/gh-bootstrap-release.sh --private-key /safe/temporary-copy.pem --prepare`；Windows 实测后用相同参数加 `--publish`。详见 `GH_首次发布_中文.md`。

## 一、更新机制

- **更新源**：公开 GitHub 仓库的最新正式 Release。客户端读取 GitHub REST API `releases/latest`，不使用个人 Token。
- **安全校验**：Release 必须附带 `update-manifest.json` 和 `update-manifest.sig`。客户端用打包时内置的 RSA 公钥验证清单签名，然后使用清单中的 SHA-256 和字节数验证更新 ZIP。签名、哈希或版本不符直接拒绝安装。
- **更新体验**：启动时自动检查、标题栏“检查更新”手动检查、显示下载进度、安装时退出主程序、独立更新器替换版本目录、重新打开主窗口。
- **回滚**：安装失败或新程序在 50 秒内没有报告窗口启动成功，更新器尝试恢复此前的安装目录并重新启动旧程序。成功更新后旧版本备份保留在安装目录同级，便于手动恢复。
- **保留配置**：原有设置及压缩期间的用户文件不在安装目录中，保留 `%LOCALAPPDATA%\ImageOptimWin` 下的配置。建议更新前完成所有正在进行的压缩任务。

注意：这属于**自动下载 + 重启更新**，不是运行中的 EXE/DLL 原地热替换。安装位置必须允许当前 Windows 用户写入（推荐用户目录，避免 `C:\Program Files` 权限问题）。第一次从 v0.8.x 升级 v0.9 必须手工部署一次，因为旧版没有更新器。

## 二、一次性设置 GitHub 更新源和签名密钥（长期设备保管私钥）

前提：Debian 安装 `openssl`、`python3`；发布到公开 GitHub Releases。**下面的密钥生成步骤应在长期可信设备执行，不能只存在临时 Debian 上；详情见 `GH_首次发布_中文.md`。**

```bash
# 在源码目录外生成私钥；千万不要上传或放进 ZIP
mkdir -p "$HOME/imageoptim-signing"
chmod 700 "$HOME/imageoptim-signing"
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 \
  -out "$HOME/imageoptim-signing/private.pem"
chmod 600 "$HOME/imageoptim-signing/private.pem"
openssl pkey -in "$HOME/imageoptim-signing/private.pem" -pubout \
  -out "$HOME/imageoptim-signing/public.pem"

cd ImageOptimWin
python3 scripts/configure-updates.py \
  --repo OWNER/ImageOptimWin \
  --public-key "$HOME/imageoptim-signing/public.pem"
```

这个命令会将仓库名称和**公钥**写入 `src/ImageOptim.Win/update-config.json`。需要用配置完成后的源码重新构建，否则已构建 ZIP 仍然不包含更新配置。

**不要在后续发布时随意生成新的密钥对。** 新版签名必须沿用现有私钥，否则已安装客户端会拒绝新版本。私钥需要离线备份，且**不要提交 GitHub、复制到任何构建产物或发送给他人**。签名操作应在可信的、保护良好的构建机器上进行。

## 三、构建 Windows 完整包

```bash
python3 scripts/check-source-config.py
python3 scripts/check-v08-regressions.py
python3 scripts/check-v081-build.py
python3 scripts/check-v09-updater.py
bash build-linux.sh
```

输出：

```text
dist/
├── win-x64/
│   ├── ImageOptim.Win.exe
│   ├── ImageOptim.Updater.exe
│   ├── update-config.json
│   └── tools/...
└── ImageOptimWin-win-x64-Bundled.zip
```

注意：v0.9 源码 ZIP 本身**不包含下载好的 Windows 压缩引擎和可执行文件**；Debian 构建脚本会在构建时抓取并打包它们。程序运行时无需单独安装压缩引擎。此脚本需要联网并准备 .NET 10 SDK。

## 四、一键发布到 GitHub Releases

先安装 GitHub CLI `gh` 并执行 `gh auth login`（或者配置有权限的 `GH_TOKEN`）。

**预览并生成签名文件，不上传：**

```bash
bash scripts/release-github.sh OWNER/ImageOptimWin 0.9.0 \
  "$HOME/imageoptim-signing/private.pem" --dry-run
```

**一条命令完成 Debian 构建、签名和发布（推荐）：**

```bash
bash scripts/build-and-release.sh OWNER/ImageOptimWin 0.9.0 \
  "$HOME/imageoptim-signing/private.pem"
```

如果想先仅生成发布素材、不上传，在上面的命令最后加上 `--dry-run`。

**已构建 ZIP 的情况下，仅执行签名与发布：**

```bash
bash scripts/release-github.sh OWNER/ImageOptimWin 0.9.0 \
  "$HOME/imageoptim-signing/private.pem"
```

上传的三个 Release 附件名称**必须固定**：

```text
ImageOptimWin-win-x64-Bundled.zip
update-manifest.json
update-manifest.sig
```

签名清单列出 `repository`、`version`、`tag`、`archive`、`sha256`、`size`。三个附件作为同一个正式 Release 发布。GitHub 的 `releases/latest` 会选择最新正式 Release（不包含 draft/prerelease），客户端据此检查新版本。

下一次发布：增加 `src/ImageOptim.Win/ImageOptim.Win.csproj` 和 `src/ImageOptim.Updater/ImageOptim.Updater.csproj` 的版本号，构建新版 ZIP，然后使用 `... 0.9.1 ...` 发布。**不要修改旧 Release 的已签名附件**；脚本发现目标 tag 已存在会拒绝覆盖。

## 五、Windows 使用方式

1. 将首次生成的 `ImageOptimWin-win-x64-Bundled.zip` 解压至例如 `%LOCALAPPDATA%\Programs\ImageOptimWin`，之后不要随意移动文件夹。
2. 双击 `ImageOptim.Win.exe`。启动会自动检查更新，也可点击标题栏“检查更新”。
3. 发现新版本后，点击“下载并安装”。下载完成且校验通过后，关闭当前窗口，独立更新器自动接管。
4. 更新器验证 ZIP、解压到相邻临时目录、将原目录重命名为备份、安装新目录、启动程序。若窗口启动失败，会尝试回滚。
5. 更新失败时查看 `%LOCALAPPDATA%\ImageOptimWin\updater.log` 和 `startup-error.log`。确保没有另一个 ImageOptimWin 实例占用旧目录。

## 六、限制 / 部署前验证

- **未在 Windows 真正运行验证**：Debian 交叉编译、Win10/11 下载、安装、回滚、杀进程和病毒防护软件兼容性必须做真实验证。当前只有源码、脚本语法和离线签名流程检查。
- GitHub API 和 Releases 需要网络访问，私有仓库及预发布版本暂不支持。
- 推荐把 APP 部署在用户可写的便携目录。安装于 `Program Files`、被其他程序锁住、跨卷安装和复杂多用户场景均不保证自动切换成功；失败会记录日志。
- 当前更新包是整个离线 ZIP，**不是差分补丁**，下载大小会包含所有压缩引擎。
- 在新版本确认窗口启动前，更新器尝试自动回滚；确认后的功能故障仍需手工回滚。请先在测试仓库和测试机验证升级路径。
- 如果你丢失签名私钥，旧客户端无法安全接受换了公钥的新版本，需要重新分发安装包。
