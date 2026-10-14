# 构建与发布脚本

本目录中的脚本只进行源码构建、纯软件自动测试、发布包生成和静态校验，不启动 WPF 工作台，不连接设备，也不会发送任何电机命令。

首次配置、发布 `v0.2.0`、安装基线以及后续版本发布的完整步骤见 [CI/CD 执行指南](CICD_GUIDE.md)。

## CI

在干净的仓库根目录执行：

```powershell
pwsh -NoProfile -File .\eng\ci.ps1
```

脚本校验 .NET 10 SDK 和每个项目的 `packages.lock.json`，随后执行锁定还原、Release 警告即错误编译以及自动测试。TRX 和覆盖率文件输出到 `artifacts/test-results/`，MSBuild 二进制日志输出到 `artifacts/build/`。`-SkipCleanCheck` 只用于开发过程中的本地验证，不应在 Gitee CI 中使用。

## 首次生成发布签名密钥

```powershell
pwsh -NoProfile -File .\eng\New-UpdateSigningKey.ps1
```

脚本生成 ECDSA P-256 密钥：

- `eng/keys/focusir-release-2026.pem` 是客户端按 `keyId` 信任的公钥，需要提交到仓库；
- `artifacts/signing/update-signing-key.pkcs8.b64` 是 PKCS#8 私钥，只能转存到 Gitee 加密变量 `UPDATE_SIGNING_KEY_PKCS8`，不得提交、打印或写入流水线日志。

若轮换密钥，应先用新的 `keyId` 生成密钥并发布同时信任新旧公钥的客户端，确认工位完成迁移后，再让发布流水线使用新私钥和 `UPDATE_SIGNING_KEY_ID`。

## 正式发布

发布只能在指向 `master` 历史的 `vX.Y.Z` 标签上执行；标签、`Directory.Build.props` 的 `VersionPrefix` 和 `CHANGELOG.md` 标题必须一致。

```powershell
$env:UPDATE_SIGNING_KEY_PKCS8 = Get-Content .\artifacts\signing\update-signing-key.pkcs8.b64 -Raw
pwsh -NoProfile -File .\eng\release.ps1 -Tag v0.2.0
```

脚本检查远端稳定版不重复且版本递增，运行完整 CI，以 `win-x64`、自包含、非裁剪模式发布：主程序保持多文件便于诊断，独立更新器使用压缩单文件以避免重复携带一整套运行时而超过 Gitee 附件限额。最终只在 `artifacts/release/` 中保留以下四个附件：

- `FocusIR.Workbench-vX.Y.Z-win-x64.zip`；
- `update-manifest.json`；
- `update-manifest.sig`；
- `SHA256SUMS.txt`。

ZIP 根目录直接包含 `FocusIR.Workbench.exe`，更新器位于 `launcher/FocusIR.Workbench.Updater.exe`。清单签名是对上传 JSON 原始 UTF-8 字节执行 ECDSA P-256/SHA-256 后得到的 IEEE P1363 fixed-field concatenation 签名，并以无 BOM 的 Base64 UTF-8 文本保存。

首次部署 `v0.2.0` 时，下载并校验 ZIP 后，将其解压到临时目录但保留原 ZIP，再运行：

```powershell
.\launcher\FocusIR.Workbench.Updater.exe --install-package <ZIP绝对路径> --version 0.2.0 --sha256 <已核对的64位SHA-256>
```

更新器会安装到当前用户的 `%LocalAppData%\ThermEye\FocusIRWorkbench`，创建开始菜单入口并启动受管版本；后续自动升级使用 `--apply-package`，该模式强制要求 SHA-256。

## Gitee 流水线接入

`.gitee/pipeline.yml` 和 `.gitee/release-pipeline.yml` 是 Gitee Go 任务模板。由于 Windows 自有主机组 ID、主机 ID和脚本类型由 Gitee 租户生成，不能安全地硬编码在仓库中；首次导入模板后必须在流水线 UI 中完成以下绑定：

1. 为两个 `shell@agent` 任务选择专用 Windows x64 主机组；节点需安装 Git、PowerShell 7 和 `global.json` 可接受的 .NET 10 SDK，且能访问 NuGet 或企业镜像。Gitee 官方目前说明 PowerShell 脚本类型仅支持阿里云导入主机，其他 Windows 主机需在界面确认支持 Bat 并用 Bat 调用 `pwsh`；必须先手动验证节点执行能力，具体步骤见完整指南。
2. CI 流水线配置 `master` 分支推送和目标为 `master` 的代码评审触发，并将其设为保护分支合并必需检查。
3. Release 流水线仅配置标签推送，正则为 `^v\d+\.\d+\.\d+$`；开启完整历史及全部标签检出，确保可验证 `origin/master`。
4. Release 流水线关联加密变量 `UPDATE_SIGNING_KEY_PKCS8`；可选设置 `UPDATE_SIGNING_KEY_ID`（默认 `focusir-release-2026`）、`MINIMUM_UPDATER_VERSION`（默认首个升级基线 `0.2.0`）、`GITEE_REPOSITORY`（默认 `warren-cao/auto-focus-factory-tool`）和只读 `GITEE_TOKEN`（仅用于提高 Release 查询限额）。
5. 在 Release 插件中确认 Tag/附件路径使用标签触发器提供的变量。模板按 Gitee 当前常用的 `${GITEE_BRANCH}` 引用；若租户把标签名暴露为其他变量，只在 UI 中映射该字段，不修改发布脚本的版本校验。

`release@gitee` 固定使用 `prerelease: false` 和 `allowUpdate: false`。任何版本、构建、测试、签名、包结构或远端重复检查失败都会在创建 Release 前终止。
