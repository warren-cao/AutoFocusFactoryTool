# 构建与发布脚本

唯一发布工作流为 GitHub **FocusIR Release (Self-hosted)**：提交源码到 `master`，推送 `vX.Y.Z` 标签即自动发布；也可从 `master` 手动运行。源码保存在 GitHub，签名附件先发布到公开 Gitee 仓库 `warren-cao/focusir-workbench-releases`，再上传相同文件到 GitHub Releases。

完整操作、凭据配置、制品恢复见 [GitHub Actions 发布指南](GITHUB_ACTIONS.md)；工位安装与人工验收见 [安装与验收](CICD_GUIDE.md)。

| 脚本 | 用途 |
| --- | --- |
| `ci.ps1` | 发布流程现有的锁定还原、警告即错误编译与纯软件测试 |
| `release.ps1` | 校验稳定标签和升级源，从 Git 生成说明，按标签版本构建、打包并签名 |
| `Publish-GiteeRelease.ps1` | 当前 Gitee 仓发布、只读诊断、制品校验、原制品恢复及 PublishFeed 静态入口更新 |
| `Publish-GitHubRelease.ps1` | 将同一套签名附件发布到 GitHub，支持显式恢复已有草稿 |
| `New-UpdateSigningKey.ps1` | 生成 ECDSA P-256 签名密钥；已有配对密钥无需重新生成 |

本地源码检查与编译：

```powershell
dotnet restore .\tools\FocusIR.Workbench\src\FocusIR.Workbench.App\FocusIR.Workbench.App.csproj --locked-mode
dotnet build .\tools\FocusIR.Workbench\src\FocusIR.Workbench.App\FocusIR.Workbench.App.csproj -c Release --no-restore --warnaserror
dotnet restore .\tools\FocusIR.Workbench\src\FocusIR.Workbench.Updater\FocusIR.Workbench.Updater.csproj --locked-mode
dotnet build .\tools\FocusIR.Workbench\src\FocusIR.Workbench.Updater\FocusIR.Workbench.Updater.csproj -c Release --no-restore --warnaserror
```

这些命令不生成或执行测试程序。现有 `ci.ps1` 包含测试执行，不用于仅编译的本地检查。所有脚本均不启动上位机、不连接镜头设备、不发送电机命令。

发布四件套固定为 `FocusIR.Workbench-vX.Y.Z-win-x64.zip`、`update-manifest.json`、`update-manifest.sig` 和 `SHA256SUMS.txt`。ZIP 根目录包含主程序，独立更新器位于 `launcher/`。清单按原始 UTF-8 字节执行 ECDSA P-256/SHA-256 签名，以 Base64 IEEE P1363 格式保存。

两个平台完成正式发布后，`PublishFeed` 复核 Gitee 正式附件，克隆独立的公开发布仓，把清单与签名按原始字节写入 `latest/`，归档 `notes/vX.Y.Z.md`，在同一次提交中普通推送 `master`，最后匿名回读校验。`GITEE_TOKEN` 还需有该仓库的代码读写权限；凭据仅注入 Git 子进程环境，不写入命令行、URL 或磁盘。

发布版本以 tag 为准，不修改源码的版本默认值或锁文件。锁定还原使用稳定的开发默认版本，编译和发布才覆盖为标签版本；已有纯软件 CI 也采用同一标签版本构建。`CHANGELOG.md` 不再必填，每版说明来自 Git 提交和附注标签，写入签名清单、安装包 `release-notes.md` 及发布仓历史文件。

公钥位于 `eng/keys/<keyId>.pem`，可提交；签名私钥仅保存在受控位置和 GitHub Actions Secret `UPDATE_SIGNING_KEY_PKCS8`，不得提交或写入日志。轮换密钥时先发布同时信任新旧公钥的客户端，再切换流水线私钥与 `UPDATE_SIGNING_KEY_ID`。`MINIMUM_UPDATER_VERSION` 是安装器能力门槛，不是旧仓迁移开关，应按安装协议要求设置。
