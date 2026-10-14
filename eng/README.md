# 构建与发布脚本

唯一发布入口为 GitHub **FocusIR Release (Self-hosted)**：先提交源码、推送 `vX.Y.Z` 标签，再从 Actions 选择 `master` 手动发布。源码保存在 GitHub，签名附件先发布到公开 Gitee 仓库 `warren-cao/focusir-workbench-releases`，再上传相同文件到 GitHub Releases。

完整操作、凭据配置、制品恢复见 [GitHub Actions 发布指南](GITHUB_ACTIONS.md)；工位安装与人工验收见 [安装与验收](CICD_GUIDE.md)。

| 脚本 | 用途 |
| --- | --- |
| `ci.ps1` | 发布流程现有的锁定还原、警告即错误编译与纯软件测试 |
| `release.ps1` | 校验标签、版本、更新日志及升级源，调用 CI，自包含打包并签名 |
| `Publish-GiteeRelease.ps1` | 当前 Gitee 仓发布、只读诊断、制品校验及原制品恢复 |
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

公钥位于 `eng/keys/<keyId>.pem`，可提交；签名私钥仅保存在受控位置和 GitHub Actions Secret `UPDATE_SIGNING_KEY_PKCS8`，不得提交或写入日志。轮换密钥时先发布同时信任新旧公钥的客户端，再切换流水线私钥与 `UPDATE_SIGNING_KEY_ID`。`MINIMUM_UPDATER_VERSION` 是安装器能力门槛，不是旧仓迁移开关，应按安装协议要求设置。
