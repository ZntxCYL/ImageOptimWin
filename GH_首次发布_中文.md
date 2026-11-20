# ImageOptimWin v0.9：临时 Debian 编译，GitHub Releases 永久托管

**Debian 仅用于 Windows 交叉构建和上传，可以在每次发布后销毁。更新服务始终是 GitHub Releases，不依赖 Debian 保持在线。**

## 1. 密钥放在哪里？

最重要的注意事项：**RSA 私钥必须在你的长期可信电脑、离线加密备份或其他长期安全密钥存储中保留原件**，不能仅留在临时 Debian VM。以后每次构建必须用同一把私钥签名，否则已经安装的 Windows 客户端无法验证新版本。发布过程只将公钥编译进 Windows 程序，GitHub 上传的 ZIP 和签名中没有私钥。

如果目前还没有密钥，可以在自己的**长期机器**上（例如 Mac/Linux/WSL）执行一次：

```bash
mkdir -p ~/imageoptim-signing
chmod 700 ~/imageoptim-signing
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out ~/imageoptim-signing/private.pem
chmod 600 ~/imageoptim-signing/private.pem
openssl pkey -in ~/imageoptim-signing/private.pem -pubout -out ~/imageoptim-signing/public.pem
```

私钥至少保留一份受保护的离线备份。不要提交 Git、上传公开仓库、粘贴到聊天里或放进应用源码包。对临时 Debian，你可以通过 `scp` 将密钥临时复制过去（也可用受控的 CI Secret 挂载为文件）：

```bash
scp ~/imageoptim-signing/private.pem user@debian-host:/home/user/imageoptim-private.pem
ssh user@debian-host 'chmod 600 ~/imageoptim-private.pem'
```

在临时服务器销毁前检查长期备份确实存在；构建结束后不再需要保留 Debian 上的私钥副本。

## 2. 使用已经授权的 gh，准备第一个 Windows 发布包

在 Debian 解压项目后：

```bash
cd ImageOptimWin
gh auth status
bash scripts/gh-bootstrap-release.sh --private-key "$HOME/imageoptim-private.pem" --prepare
```

默认使用**当前 Debian 的 `gh` 登录账号**下的 `ImageOptimWin` 仓库。例如 `gh api user --jq .login` 是 `ZntxCYL`，则目标为 `ZntxCYL/ImageOptimWin`。也可以显式指定 `--repo OWNER/REPO`。

`--prepare` 会校验项目版本和历史补丁，写入仓库/公钥，调用 `build-linux.sh` 自动构建 Windows x64 主程序、独立更新器和压缩引擎，并生成通过 RSA 签名校验的 Release 素材。此步骤**不修改 GitHub**，输出在 `dist/`。

构建完成后先将 `dist/ImageOptimWin-win-x64-Bundled.zip` 拿到真实 Windows 机器上，检查启动、压缩和独立 Updater 的安装/回滚流程。Debian 无法运行 WPF 窗口。

## 3. 正式发布到 GitHub Releases

仅在 Windows 验证通过后，明确执行：

```bash
bash scripts/gh-bootstrap-release.sh --private-key "$HOME/imageoptim-private.pem" --publish
```

首次发布时，脚本会**在本地构建和签名检查成功之后**检查 GitHub 仓库；如不存在，会创建一个 **公开仓库** 并初始化 README，再发布正式 Release `v0.9.0`，上传：

- `ImageOptimWin-win-x64-Bundled.zip`
- `update-manifest.json`
- `update-manifest.sig`

若仓库已存在且是私有，或已存在同版本 Release，脚本会停止，不覆盖。脚本不将源码推送进仓库。未来 Windows 客户端通过 GitHub API 和 Releases 下载更新，无须服务器在线或额外 Token。

**注意：** `--publish` 会重新构建打包，最好对本次要发布的产物再做 Windows 测试；或在已测完的构建产物上手工运行 `scripts/release-github.sh`，避免两次构建的差异。

## 4. 下次换一台 Debian 继续发布

在新的 Debian 机器解压最新源码、准备好同一把私钥副本，更新主程序和 Updater 两个 `.csproj` 内 `<Version>`（例如 `0.9.1`），然后使用：

```bash
bash scripts/gh-bootstrap-release.sh --private-key "$HOME/imageoptim-private.pem" --version 0.9.1 --prepare
# Windows 验证后
bash scripts/gh-bootstrap-release.sh --private-key "$HOME/imageoptim-private.pem" --version 0.9.1 --publish
```

不要更换旧版本的 GitHub 仓库或 RSA 公钥。如果你丢失原私钥，原有 Windows 客户端无法通过现有信任链继续自动升级，需要重新分发具有新公钥的安装包。

## 5. 实测状态

源码静态检查、签名生成与验证可以在 Debian 执行；Windows WPF 启动和更新器回滚必须上 Windows 实测。当前源码包不代表已经生成经过 Windows 实测的可执行发布包。
