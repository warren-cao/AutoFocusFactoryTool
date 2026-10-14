# FocusIR Workbench CI/CD 执行指南

**历史 Gitee Go 方案：**当前已切换为 GitHub 唯一源码仓（可私有）和 Gitee 公开二进制发布仓，不再向 Gitee 推送源码。现行步骤以 [GitHub Actions 发布指南](GITHUB_ACTIONS.md) 为准；本页下述同仓源码/流水线配置仅留存参考，不再执行。

**没有 Gitee 企业版主机执行能力时，请使用 [GitHub Actions 发布指南](GITHUB_ACTIONS.md)。**该方式使用 GitHub 云端 Windows 节点，仍发布到原 Gitee 升级源；下面的主机组配置仅适用于有相应 Gitee Go 权限的账号。

执行顺序：推送代码 → 配置 Windows 节点和发布凭证 → 手动通过 CI → 开启标签触发 → 推送 `v0.2.0` → 检查四个附件 → 工位安装基线。

正式发布优先导入 `.workflow/focusir-workbench-release.yml`，它使用本工程的 Windows 构建、测试、签名打包与 Gitee Release 流程。原 `.workflow/流水线-202610131538.yml` 为不适用于本工程的 Maven 示例，不应启用。`.gitee/release-pipeline.yml` 作为较简模板保留；两者只能选择一条启用正式发布，避免同标签重复执行。

仓库中的 YAML 是任务模板。提交文件不会自动完成租户内的主机、凭证、代码源、触发规则或制品传递配置；这些设置需要在 Gitee 流水线页面保存。下面的界面名称以 Gitee 官方文档为依据，实际入口可能随账号版本有所不同。

## 1. 推送已提交代码

在本地仓库根目录运行：

```powershell
Set-Location F:\Project\auto-focus-factory-tool
git status --short
git log -1 --oneline
git push origin master
```

`git status --short` 应没有输出。如 `master` 已设置保护规则并禁止直接推送，将本次提交推送到功能分支后通过 PR 合并；后续所有标签都应指向已合入 `master` 的提交。

此时先不要推送发布标签，也不要手工创建同名 Release。发布阶段会创建 Release，提前创建会导致重复检查失败。

## 2. 准备 Windows 构建节点

准备专用 Windows x64 主机。安装 Git、PowerShell 7（命令为 `pwsh`）、正式版 .NET 10 SDK。仓库 `global.json` 要求 `10.0.100`，允许同属 .NET 10.0 的更新 feature band；本地已使用 `10.0.401` 编译验证。

使用流水线实际运行账号，在仓库根目录检查：

```powershell
git --version
pwsh --version
dotnet --version
dotnet --list-sdks
```

运行账号需要能读取源码、写入工作区及 NuGet 缓存，并访问 `gitee.com`、Gitee 附件 CDN `foruda.gitee.com`、NuGet 包源及 .NET 运行时包下载源。正式发布包含运行时，工位不需要安装 SDK。

在 Gitee 的主机管理中创建主机组，并关联当前仓库/项目。按界面为目标 Windows 主机生成的安装命令添加节点，确认在线；不使用 Linux 容器执行最终 WPF 发布。

**Windows 执行方式必须先验证：**官方 Shell 插件支持 Shell、Bat、PowerShell，但目前说明 PowerShell 类型仅在阿里云导入的主机上支持。阿里云 Windows 主机可选 PowerShell；其他 Windows 主机若能选择 Bat，则用 Bat 调用 PowerShell 7，并保留退出码，例如：

```bat
pwsh -NoProfile -File .\eng\ci.ps1
exit /b %ERRORLEVEL%
```

如果租户不提供可用的 Windows/Bat 执行方式，需要先开通对应能力或接入 Windows 外部执行器；不能把 YAML 里的 Windows 命令直接放进 Linux Shell 后继续发版。

克隆源码的账号应有仓库读取权限；如果平台要求 SSH 克隆，给实际运行账号配置 SSH 密钥，并把公钥加入 Gitee。开启代码克隆和完整历史，确保能取得 `origin/master` 及全部标签。发布运行必须检出对应标签提交，而不是始终检出 `master` 最新提交。

## 3. 配置签名密钥和权限

本次已经生成配对密钥，无需再生成：

- 已提交公钥：`eng/keys/focusir-release-2026.pem`。
- 本机私钥：`artifacts/signing/update-signing-key.pkcs8.b64`，Git 已忽略。

在 Gitee Release 流水线的加密变量/凭证页面保存以下配置。确认机密变量实际注入 Windows 任务的进程环境，发布脚本读取 `$env:UPDATE_SIGNING_KEY_PKCS8`。

| 名称 | 类型 | 本次值 |
| --- | --- | --- |
| `UPDATE_SIGNING_KEY_PKCS8` | 加密变量，必填 | 本机 `.b64` 私钥文件的完整文本 |
| `UPDATE_SIGNING_KEY_ID` | 普通变量，可选 | `focusir-release-2026` |
| `MINIMUM_UPDATER_VERSION` | 普通变量，可选 | `0.2.0` |
| `GITEE_REPOSITORY` | 普通变量，可选 | `warren-cao/auto-focus-factory-tool` |

只把签名私钥提供给受保护的发布流水线，不提供给 PR CI。确认加密变量没有作为普通文本写入 YAML、发布说明或日志。保留安全备份；切勿用 `New-UpdateSigningKey.ps1 -Force` 无意覆盖已有配对密钥。

公开仓库的版本查询默认匿名，不需要 `GITEE_TOKEN`。真正创建 Release 和上传附件所需的写权限在 Gitee Release 插件的代码源授权/凭证中配置；读取 API 的可选令牌不等同于插件发版权限。

本机私钥已经由生成过程保存到忽略目录。完成安全备份和平台配置后可人工移除本机副本；删除前应确认平台凭证已经保存。

## 4. 配置并运行 CI

在仓库“流水线”创建 `FocusIR Workbench CI`，导入 `.gitee/pipeline.yml`，或在代码视图粘贴模板内容。

在任务界面确认并保存：

1. 代码源是本仓库，执行主机是前述 Windows x64 节点，开启克隆。
2. 执行命令是 `pwsh -NoProfile -File .\eng\ci.ps1`；Bat 模式按第 2 节传递退出码。
3. 触发事件包含 `master` 分支推送，以及目标分支为 `master` 的代码评审。模板只预置了分支推送，PR 触发需在 UI 补充。
4. 归档 `artifacts/test-results/**` 和 `artifacts/build/**`；TRX、Cobertura 覆盖率文件及 MSBuild binlog 均需能下载。
5. 在设置保护分支之前，先手动运行一次 CI，确认节点和源码克隆正常，再设置 CI 成功为 PR 合并条件（依账号能力配置）。

这一步按顺序执行干净工作区检查、SDK 检查、锁定还原、Release 警告即错误编译、纯软件测试。当前测试集共 47 项；本次 UI 改动另外通过源码检查与编译验证。流水线不连接真实设备。

成功标准：所有阶段成功、编译无警告、测试无失败、日志和测试制品可下载。正式 CI 不使用 `-SkipCleanCheck`。若平台把生成的任务脚本放进 Git 工作区导致脏状态，应调整平台临时脚本位置，而不是忽略所有源码变更。

## 5. 配置 Release 流水线

创建 `FocusIR Workbench Release`，导入 `.workflow/focusir-workbench-release.yml`。

**模板默认手动触发。需要自动发布时，在 UI 改成标签推送触发**，并设置正则 `^v\d+\.\d+\.\d+$`。不要配置每次 `master` 推送都发布。限制发布标签创建权限；账号提供保护标签能力时，保护 `v*`。

也可保留手动发布：先推送版本标签，在运行页面选择对应标签，并核对 `${GITEE_BRANCH}` 的实际值。构建脚本仍执行完整版本检查。默认 `hostGroupID.ID` 为空，必须在 Gitee 任务界面选择自己的 Windows 主机组；不能使用示例 ID。

第一阶段“构建、签名与打包”配置：

- Windows 节点、完整克隆/标签检出、机密环境变量。
- 命令：`pwsh -NoProfile -File .\eng\release.ps1 -Tag "${GITEE_BRANCH}"`。
- 新文件中的 `script` 包含 PowerShell 的 Windows/凭证检查，任务脚本类型应选 PowerShell。若主机只能使用 Bat，把整段脚本替换为下述命令；不要将 PowerShell 原文交给 cmd 执行：

```bat
pwsh -NoProfile -File .\eng\release.ps1 -Tag "${GITEE_BRANCH}"
exit /b %ERRORLEVEL%
```

- 新文件不写入任何私钥；`UPDATE_SIGNING_KEY_PKCS8` 必须通过平台的加密环境变量注入。
- `${GITEE_BRANCH}` 需在当前租户的标签事件中实际解析为 `v0.2.0`。用平台变量选择器核对；若提供其他标签变量，修改任务及下一阶段全部引用，不能传 `master` 或 `refs/tags/v0.2.0`。
- 构建制品标识 `FOCUSIR_RELEASE`，路径 `artifacts/release/**`。
- 诊断制品标识 `FOCUSIR_RELEASE_DIAGNOSTICS`，收集 `artifacts/test-results/**` 和 `artifacts/build/**`。
- 每次运行使用新的工作区或新的空发布输出目录。脚本拒绝向已有非空 `artifacts/release` 写入；保留失败制品后，使用新工作区重试。

第二阶段“发布 Gitee Release”配置：

| 字段 | 值 |
| --- | --- |
| 插件 | `release@gitee` |
| 关联仓库 | 本仓库 |
| Release 名称 | `FocusIR Workbench ${GITEE_BRANCH}` |
| 标签 | `${GITEE_BRANCH}` |
| 描述 | `CHANGELOG.md` |
| 上游制品 | `FOCUSIR_RELEASE` |
| 预发布 | `false` |
| 允许覆盖 | `false` |

附件字段使用平台名称 `assertFiles`，固定四项：

```text
artifacts/release/FocusIR.Workbench-${GITEE_BRANCH}-win-x64.zip
artifacts/release/update-manifest.json
artifacts/release/update-manifest.sig
artifacts/release/SHA256SUMS.txt
```

必须核对 Windows 主机任务产生的文件被收集到上游制品，并在 Release 插件工作区恢复为上述相对路径；不同阶段的磁盘不会自动共享。源代码仓库里不会有 `artifacts` 文件。若任务插件不支持收集远程主机文件，需通过平台支持的制品上传/下载步骤传递，再配置相同相对路径。

第一阶段包含 CI、双程序发布、ZIP 打包、清单签名及复核；失败时第二阶段不应执行。不要把 `allowUpdate` 改为 `true` 来绕过重复版本检查。

## 6. 发布首版 v0.2.0

先完成节点、凭证及触发规则配置，且 CI 已通过。随后在本地执行：

```powershell
Set-Location F:\Project\auto-focus-factory-tool
git switch master
git fetch origin --tags
git pull --ff-only origin master
git status --short
pwsh -NoProfile -File .\eng\ci.ps1
```

上一步须成功，工作区须干净。确认 `Directory.Build.props` 的 `VersionPrefix` 为 `0.2.0`，`CHANGELOG.md` 包含 `[0.2.0]`，Gitee 没有 `v0.2.0` 同名 Release/标签，再执行：

```powershell
git tag -a v0.2.0 -m "FocusIR Workbench v0.2.0"
git push origin v0.2.0
```

这次标签推送触发 Release 流水线。观察检查的提交和标签值一致，等待两个阶段均成功。由于构建节点并发受限，建议 CI 与 Release 在同一节点串行运行且不共用工作目录。

发布后在仓库 Release 页面检查四个附件齐全。可用匿名 API 核对：

```powershell
$release = Invoke-RestMethod 'https://gitee.com/api/v5/repos/warren-cao/auto-focus-factory-tool/releases/latest'
$release | Select-Object tag_name, prerelease
Invoke-RestMethod ("https://gitee.com/api/v5/repos/warren-cao/auto-focus-factory-tool/releases/" + $release.id + '/attach_files?page=1&per_page=100') |
    Select-Object name, browser_download_url
```

应看到 `v0.2.0`、非预发布状态和四个对应附件。用未登录浏览器确认 ZIP、清单与签名可下载，且清单中的 ZIP 地址确实可访问。

本地打包实测约 90 MiB；最终以流水线 ZIP 大小为准，检查账号当前单附件和总容量配额。不要把 Gitee 自动生成的源码 ZIP 当成程序升级包。

## 7. 安装工位基线

旧 `v0.1.0` 无法自行升级到首个基线。下载发布的四个附件，放到同一目录，按 SHA256SUMS 检查完整性：

```powershell
Set-Location D:\Downloads\FocusIR-v0.2.0
Get-Content .\SHA256SUMS.txt | ForEach-Object {
    $parts = $_ -split '\s+', 2
    if ($parts.Count -ne 2) { throw '校验文件格式错误。' }
    $actual = (Get-FileHash -LiteralPath $parts[1] -Algorithm SHA256).Hash
    if ($actual -ine $parts[0]) { throw ("文件校验失败：" + $parts[1]) }
}
$zipPath = (Resolve-Path .\FocusIR.Workbench-v0.2.0-win-x64.zip).Path
$packageHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Expand-Archive -LiteralPath $zipPath -DestinationPath .\baseline
& .\baseline\launcher\FocusIR.Workbench.Updater.exe --install-package $zipPath --version 0.2.0 --sha256 $packageHash
```

SHA-256 检测传输损坏；初次信任来自确认这是官方仓库中的 Release。后续应用内升级还会使用基线程序中嵌入的公钥验签。

人工确认窗口能够显示，帮助 → 关于工作台显示 `v0.2.0`，开始菜单存在固定启动器入口。程序安装在 `%LocalAppData%\ThermEye\FocusIRWorkbench`，保留用户配置；自动升级健康确认等待 30 秒。

同为 `0.2.0` 时，“检查更新”显示已是最新版本是正常结果。下一次发布更高版本后再验证实际升级。安装前完成设备任务并断开设备；真实镜头效果由人工验收。

## 8. 后续发布，例如 v0.2.1

将 `Directory.Build.props` 中版本改为 `0.2.1`，在 `CHANGELOG.md` 新增 `[0.2.1]` 的说明。项目引用版本变化也可能改变锁文件，因此在开发机执行：

```powershell
dotnet restore .\FocusIR.Workbench.sln --force-evaluate
```

检查并提交版本、源码、更新日志及必要的 `packages.lock.json` 变更，推送/通过 PR 合入 `master`，等待 CI 成功。然后：

```powershell
git switch master
git pull --ff-only origin master
git tag -a v0.2.1 -m "FocusIR Workbench v0.2.1"
git push origin v0.2.1
```

`MINIMUM_UPDATER_VERSION` 保持 `0.2.0`，除非安装协议确实需要更高更新器。生产版保持原密钥；轮换时先发布同时包含新旧公钥的过渡版，再更换签名私钥。

## 9. 常见故障与处理

| 症状 | 处理 |
| --- | --- |
| 标签推送后没有运行 | 检查 Release 模板默认手动触发是否已改为标签自动触发、规则是否匹配、代码源 WebHook 是否生效。 |
| 找不到 pwsh/dotnet/git | 在流水线实际账号下检查 PATH；安装后重启节点服务，不能只在自己的终端验证。 |
| PowerShell 类型不可选 | 核对主机导入方式；其他 Windows 主机尝试 UI 支持的 Bat 调用 pwsh。无可用 Windows 执行能力时先解决节点接入。 |
| NU1004 锁文件不一致 | 在开发机还原并提交更新后的锁文件；发布任务继续使用 locked-mode。 |
| NuGet 还原失败 | 检查节点网络、代理、可信镜像和运行时包下载，不在正式发布中关闭锁定检查。 |
| 标签不属于 master / 不指向 HEAD | 检查完整历史、origin/master 和标签检出；不能在仍检出 master 的运行中传另一提交的标签。 |
| 缺少私钥 | 检查机密变量是否已注入 Windows 进程环境，勿打印值诊断。 |
| 签名与公钥不匹配 | 确认 keyId、公钥文件和平台私钥是同一配对；不要仅重生成一侧。 |
| 发布输出目录非空 | 下载并保留失败制品，使用新的工作区重跑；不删除签名备份。 |
| Release 已存在 | 不覆盖。若同名 Release 因附件上传失败而不完整，先人工确认尚未分发，再单独处理残留；正常修复应发更高版本。 |
| Release 插件找不到附件 | 检查上游制品已上传并恢复为 artifacts/release 下的相对路径；不能直接访问另一主机的磁盘。 |
| 附件上传超限 | 核对账号当前配额和实际 ZIP 大小；保留现有优化发布方式，调整配额或托管能力。 |
| 自动升级失败 | 查看用户目录 logs/updater.log，保留失败包，检查版本、签名、权限与健康启动；需要时执行固定 launcher 的 --rollback。 |

## 官方资料

- [Gitee 代码提交、标签与代码评审触发](https://help.gitee.com/enterprise/pipeline/source/trigger/)
- [Gitee 主机执行插件及脚本类型限制](https://help.gitee.com/enterprise/pipeline/plugin/shell-script-run)
- [Gitee Windows 主机纳管与主机组](https://help.gitee.com/enterprise/pipeline/enterprise-setup/host/introduce)
- [Gitee Release 插件字段与附件说明](https://help.gitee.com/enterprise/pipeline/plugin/gitee-release)
