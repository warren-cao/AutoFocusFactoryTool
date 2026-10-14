# GitHub Actions 构建与发布

支持两条发布路径：GitHub 云端 Windows 节点，或自托管 Windows 节点 `DESKTOP-AI3E9IB`。两者均向 Gitee 发布正式升级包，并把相同的四个文件发布到 GitHub Releases，无需 Gitee 企业版。

当前结构：**GitHub 是唯一源码仓（可以私有），Gitee 是公开的二进制发布仓。**不再向 Gitee 推送应用源码、master 或源码标签；Gitee 只需保留一个发布说明 README 提交，Release 标签由 API 在该仓库的锚点提交上创建。两个平台的同名版本标签不再要求提交 SHA 相同，应用源码版本始终由 GitHub 标签确定。

- GitHub 仓库：<https://github.com/warren-cao/AutoFocusFactoryTool>
- 云端工作流：`.github/workflows/release.yml`
- 自托管工作流：`.github/workflows/release-self-hosted.yml`（仅手动触发）
- 原制品收尾工作流：`.github/workflows/finalize-release.yml`（仅手动触发，不编译）
- 新的公开升级源：<https://gitee.com/warren-cao/focusir-workbench-releases/releases>
- 旧客户端桥接源：<https://gitee.com/warren-cao/auto-focus-factory-tool/releases>（迁移完成前保留）

## 1. 一次性配置 Secrets

进入 GitHub 仓库 → Settings → Secrets and variables → Actions → New repository secret。

| Secret | 内容 |
| --- | --- |
| `UPDATE_SIGNING_KEY_PKCS8` | 现有 `artifacts/signing/update-signing-key.pkcs8.b64` 的完整文本，须与 `eng/keys/focusir-release-2026.pem` 配对 |
| `GITEE_TOKEN` | Gitee 私人令牌，须有新公开发布仓及旧桥接仓的 Release 创建/修改和附件上传权限；不需要访问 GitHub 源码 |

Gitee 令牌在 <https://gitee.com/profile/personal_access_tokens> 创建。不要用 GitHub Token 代替。私钥不需要重新生成，也不要提交到仓库；只在平台的机密字段粘贴其值。

工作流将 GitHub Token 限制在发布 job 的 `contents: write`；使用内置 `github.token` 发布 GitHub Release，无需额外 GitHub PAT。第三方 Action 实际只使用 GitHub 官方 actions，并固定到已核对的完整提交 SHA。

## 2. 准备源码和版本标签

GitHub 保存完整源码、master 和版本标签。Gitee 发布仓必须公开，并已有至少一个 README 初始化提交；可以没有任何应用源码。脚本自动读取 Gitee 默认分支作为发布锚点，也可通过仓库 Actions Variable `GITEE_RELEASE_TARGET` 指定该发布仓里的分支/提交。不能把 GitHub 源码 SHA 填入这个变量。

版本文件和更新日志必须匹配待发布版本，然后只向 GitHub 推送。例如准备桥接版本 `v0.2.1`（旧 `v0.2.0` 标签不得移动）：

```powershell
Set-Location F:\Project\auto-focus-factory-tool
git status --short
git push github master
git tag -a v0.2.1 -m "FocusIR Workbench v0.2.1 migration bridge"
git push github v0.2.1
```

最后一条命令触发云端 GitHub Actions。本机已将默认 `origin` 切为 GitHub，并保留 `github` 兼容别名；旧 Gitee 远端改名为 `gitee-legacy`，只用于读取历史，其本机 push URL 已禁用。其他已有克隆请先检查 `git remote -v`，确认目标为 GitHub 后再推送，不要把应用源码上传到新/旧 Gitee。不要预先创建同名 Gitee Release。

本机此前已确认 GitHub 连接需要 HTTP/1.1 时，可把相关 push 改为：

```powershell
git -c http.version=HTTP/1.1 -c http.sslBackend=schannel push github master
git -c http.version=HTTP/1.1 -c http.sslBackend=schannel push github v0.2.1
```

后续版本先更新 `Directory.Build.props`、`CHANGELOG.md`，执行 `dotnet restore .\FocusIR.Workbench.sln --force-evaluate` 并检查提交相应锁文件，然后仅在 GitHub 提交、推送和打标签。已发布的 GitHub 源码标签不能重新指向另一提交。

## 3. 手动运行与只构建模式

进入 Actions → FocusIR Release → Run workflow：

1. 工作流分支选择 `master`。
2. `tag` 填写已推送到 GitHub 的完整版本标签，例如 `v0.2.1`，不能填写 master、提交 SHA 或预发布标签。
3. `publish=true`：完整发布到 Gitee 和 GitHub。
4. `publish=false`：执行测试、构建、签名和成品检查，上传 Actions Artifacts，不创建任何远端 Release；仍需要签名 Secret。

只构建模式仍保留现有发布脚本的版本递增、远端重复版本检查，因此不能用于覆盖已发布版本。测试新工作流可使用尚未发布的版本标签。

构建脚本及两个平台的上传脚本均固定取自本次工作流的提交（`github.workflow_sha`），再通过 `-SourceDirectory` 对选中的版本标签源码构建和验签。工具位于 runner 临时目录，不污染源码工作区。这样可以在 master 修复 CI 工具后，手动发布尚未成功发布的旧标签，而不移动标签、改变应用源码或跳过安全检查。版本号、更新日志、验签公钥与编译内容仍全部来自所选标签。

如果旧构建因工具脚本错误失败，修复推送后不要只点击旧记录的 Re-run；应在 Actions → FocusIR Release → Run workflow 选择 `master`，使用包含修复的新工作流。只有没有遗留 Release 时才重新走完整发布；已有附件时按下文的显式收尾流程处理。

## 4. 工作流执行内容

1. 校验严格的三段式稳定标签，以及手动运行的工作流分支。
2. 完整检出标签与所有历史，确认提交属于 `origin/master`。
3. 在 `windows-2025` 安装符合 `global.json` 的 .NET 10 SDK，按锁文件缓存 NuGet 包。
4. 正式发布模式检查 Gitee 发布仓公开、README 锚点提交有效、目标 Release 未发布；不读取或同步 Gitee 应用源码。
5. 调用 `eng/release.ps1`，执行锁定还原、Release 警告即错误编译、现有纯软件测试、两程序自包含发布、ZIP 打包与 ECDSA 签名。
6. 上传诊断制品（14 天）和签名升级包制品（30 天），仅收集指定目录，不上传私钥。
7. 调用 `eng/Publish-GiteeRelease.ps1`，复核四个文件的签名和摘要，先创建预发布 Release，再逐个上传 ZIP、签名、摘要和清单；确认附件列表完整后转为正式版。
8. 使用 `eng/Publish-GitHubRelease.ps1` 调用 GitHub 官方 REST API，先创建草稿并上传相同四个文件，确认各附件的名称、大小和状态后转正式版。无需预装 GitHub CLI。

只允许 tag push 和手动触发，不因 PR 执行签名或发版。发布任务串行运行，不取消正在上传的任务。GitHub concurrency 最多保留一个等待任务；不要密集推送多个发布标签，逐版确认完成后再发布下一版。

## 5. 查看结果

成功后在 Gitee 和 GitHub Releases 都应看到以下文件：

```text
FocusIR.Workbench-v0.2.0-win-x64.zip
update-manifest.json
update-manifest.sig
SHA256SUMS.txt
```

上位机仍匿名从公开 Gitee 查询和下载升级，不访问 GitHub。GitHub Release 仅作为维护者的同一产物备份：GitHub 仓库改为私有后，其 Release 和附件也不再是公开下载入口，但不会影响 Gitee 升级。

Actions 页面始终尝试归档测试结果和 MSBuild 日志；构建、测试、签名或成品校验失败时，不会进入发版步骤。首版工位安装和真实设备人工验收步骤见 [CI/CD 执行指南](CICD_GUIDE.md)。

## 6. 重试与故障处理

- **缺少 Secret：**完成第 1 节配置后重新运行；不要把私钥或令牌写入日志排查。
- **Gitee 无有效锚点：**在发布仓创建仅含 README 的初始化提交，核对默认分支或 `GITEE_RELEASE_TARGET`。不要推送 GitHub 源码解决。
- **Gitee 返回 401/403：**核对令牌有效性、目标仓库和 Release/附件权限；公开仓库能读不代表令牌有写权限。
- **Gitee 上传中断：**已创建的版本保持预发布；不要启用原 Gitee Go 发布任务并行覆盖。先检查残留版本和附件，确认未作为正式版分发，再人工移除本次不完整预发布记录，或修复后发布更高版本。脚本不会自动删除远端记录。
- **Gitee 附件完整、转正式版 HTTP 400：**这不代表 GitHub 上传失败。GitHub 步骤会被跳过；旧脚本 PATCH 缺少 Gitee 必填的 `tag_name`、`name`、`body`。新版已补齐，使用下文 `FinalizeExisting` 对同一批原始制品收尾，禁止重新打包或覆盖附件。
- **Gitee 成功、GitHub 镜像失败：**Gitee 已是有效发布，不能重跑整条流程来覆盖它。下载原始签名制品，只调用 `Publish-GitHubRelease.ps1` 补发。若 GitHub 已存在草稿，该脚本仍会拒绝覆盖，先人工核对草稿及附件，不自动删除。
- **附件超出账号额度：**核对 Gitee 当前附件限制。已有发布脚本使用压缩单文件更新器和简体中文资源；不在工作流里偷偷更换客户端更新源。
- **重复版本被拒绝：**这是正式发布的保护行为；正常修复使用更高版本，不移动标签。

Gitee 发布上传脚本还提供 `-Mode Validate`，可对已有 `artifacts/release` 进行离线结构、签名和摘要检查；`-Mode CheckRemote` 仅做凭证、源码提交和重复 Release 的远端只读检查。

## 7. 使用 DESKTOP-AI3E9IB 自托管发布

已核对该 runner 的注册标签为 `self-hosted`、`Windows`、`X64`，名称为 `DESKTOP-AI3E9IB`。名称不等于标签，不能直接把名称填入 `runs-on`，否则会一直排队。新工作流匹配这三个标签，并在第一步核对 `RUNNER_NAME`；只有该主机才继续构建和加载机密。如果以后增加同标签主机，应为目标主机配置专用标签，再修改 `runs-on`，避免错误调度。

准备要求：

- runner 在线，并使用独立构建目录和普通低权限账号；不要把用户项目或私钥存放在 Actions checkout 目录中，checkout 会清理自己的工作区。
- runner 进程的 PATH 中有 PowerShell **7.4+**（`pwsh`）和 Git。安装或修改 PATH 后重启 runner 进程/服务。Windows PowerShell 5.1 不能替代 `pwsh`。
- 使用兼容固定版本官方 actions 的最新稳定 runner。更新 runner 本身不等于安装 PowerShell。
- `.NET` SDK 由工作流按 `global.json` 安装到 runner 临时目录，不要求管理员权限；主机应能访问 GitHub、GitHub Artifacts、微软 SDK 下载站、NuGet 和 Gitee。
- 两个仓库 Secrets 与云端工作流相同，无需在 runner 本地持久化 Gitee Token 或签名私钥。
- 自托管主机是持久环境，不能把它作为处理外部不可信 PR 的通用节点，也不能连接镜头执行自动工厂任务。工作流仅运行既有纯软件测试，不启动上位机或驱动电机。

首次验证步骤：

1. 把新工作流和工具提交推送到 GitHub `master`，不移动 `v0.2.0` 标签。
2. Actions → **FocusIR Release (Self-hosted)** → **Run workflow**。
3. 分支选择 `master`，tag 填新源码对应的 `v0.2.1`，保持 **publish=false**。`v0.2.0` 的原包收尾使用第 9 节专用工作流，不重建。
4. 查看 PowerShell 前置检查和实际 runner 名称，再查看 Gitee 只读诊断、编译、既有软件测试和签名打包结果。
5. 下载 `focusir-self-hosted-release-...` 和 `focusir-self-hosted-diagnostics-...` 制品。此模式不会创建、上传、修改或删除远端 Release；Gitee 已有不完整预发布时仍可以进行只读诊断。它验证基本 API 连通性，不验证大 ZIP 上传。

正式发布时，再手动运行同一工作流并选择 **publish=true**，执行完整 Gitee 和 GitHub 发布。

**已有预发布不能直接重发：**先核对状态和附件。四个附件均已上传的版本，使用下文收尾模式保留原始文件；缺失附件的版本先人工处理，或准备更高版本。脚本不会自动删除、覆盖或续传现有 Release。对已正式分发的版本，必须发布更高版本。

云端和自托管工作流共用 `focusir-stable-release` 并发锁。标签 push 仍触发原云端流程；自托管方案仅手动触发。新版本如需只在自托管构建，先推送并等待自动云端流程结束，或事先在 Actions 页面停用云端工作流；不要让两个流程争相发布同一标签。

## 8. 查看 Gitee 上传诊断

`eng/Publish-GiteeRelease.ps1` 的联网模式都会在 `artifacts/release-logs/gitee-*.jsonl` 留下结构化日志，同时显示在 Actions 输出中。两个工作流都在最后以 `always()` 收集这些日志，所以上传失败时仍能下载。

- `session`：模式、标签、runner 名称、PowerShell 版本、请求超时，以及是否设置了代理环境变量。仅记录代理是否配置，不记录代理地址；未设置环境变量不代表系统没有代理。
- `request-start`：请求 ID、HTTP 方法、无查询参数的 API 路径、附件名称、文件字节数和请求长度；`bodyFields` 仅列出非凭据字段名，不记录值。
- `upload-progress`：每约 15 秒记录已写入传输流的字节数和耗时。`waitingForResponse=true` 表示文件字节已经写入流，**不代表服务端已完整接收、持久化或创建附件**。
- `request-response`：HTTP 状态码、总耗时和上传计数。401/403 优先核对凭证及权限，413 核对体积限制，429 核对频率限制，5xx 核对服务端情况。
- `request-error`：超时/取消或传输错误分类、异常链类型、HRESULT，以及可用的 `HttpRequestError`、Socket 错误码；不输出原始异常消息、堆栈、请求头、正文、Token、私钥或代理凭据。
- `response-parse-error`：服务端返回了无法解析的 JSON，保留请求 ID，但不输出原始正文。
- `existing-release`：只读诊断查询到的已有 Release ID、预发布状态和附件数量。
- `release-provenance`：GitHub 源码 SHA 与 Gitee 发布仓锚点 SHA，二者用途不同，不要求相同。
- `remote-artifact-verified`：收尾时匿名重新读取远端附件，确认字节数和 SHA-256 与原始签名制品完全相同。

默认每个 HTTP 请求仍为 **15 分钟**，可通过 `-RequestTimeoutMinutes` 显式调整（1–120 分钟）；只延长超时不是链路问题的修复。发生超时时先核对附件实际状态，再决定人工处理，不自动重试可能产生重复附件的 POST。

公开仓只读诊断可以匿名执行；其他联网模式需要通过安全机制设置 `GITEE_TOKEN`，不要把其值写进命令历史：

```powershell
./eng/Publish-GiteeRelease.ps1 -Tag v0.2.0 -Repository warren-cao/auto-focus-factory-tool -TargetCommit (git rev-parse 'v0.2.0^{}') -Mode Diagnose
```

## 9. 保留原制品完成这次 v0.2.0 发布

本次失败发生在运行 `38519225911`：四个附件已上传到 Gitee Release `1204616`，但转正式版返回 400，GitHub 步骤为 `skipped`。不要重跑完整构建、删除附件或创建第二个同名 Release。

**推荐直接使用 Actions，无需在本机输入令牌：**

1. 把修复推送到 GitHub master 后，Actions → **FocusIR Finalize Existing Release** → Run workflow。
2. 分支选 `master`，`tag=v0.2.0`，`source_run_id=38519225911`，`gitee_release_id=1204616`；`gitee_repository` 选旧仓 `warren-cao/auto-focus-factory-tool`，`artifact_kind=release`。
3. 可先保留 `publish=false`，验证原运行来源、制品完整性、签名，并匿名下载 Gitee 四个附件比对哈希，不修改任何远端发布。
4. 确认后同样输入选择 `publish=true`：使用原运行中的不可变制品，完整校验远端附件后只补做 Gitee 转正式版，再上传同样四个文件到 GitHub。使用现有 Secrets 和临时 GitHub Token，无需新增 PAT、SDK 或签名私钥。
5. 不要从原失败记录点击 Re-run；旧记录仍使用旧工具。新工作流也不自动删除或覆盖 GitHub 已有草稿。

也可以由维护者手动执行：

1. 从该次运行下载 `focusir-self-hosted-release-v0.2.0-1` 制品。
2. 只解开 Actions 制品的外层 ZIP，确保目录直接包含四个原始文件；不要解开里面的程序 ZIP，更不要重新生成签名/清单。
3. 使用 GitHub master 上最新的两个上传脚本。让 `GITEE_TOKEN` 和 `GH_TOKEN` 通过安全机制注入当前进程环境，不在命令中写明令牌；本机 `GH_TOKEN` 需要目标源码仓的 contents 写权限，Actions 的临时 token 不能在本机凭空复用。
4. 先显式收尾，再只补发 GitHub：

```powershell
$originalFiles = 'F:\Project\auto-focus-factory-tool\artifacts\recovery-38519225911'
./eng/Publish-GiteeRelease.ps1 -Tag v0.2.0 -Repository warren-cao/auto-focus-factory-tool -SourceCommit (git rev-parse 'v0.2.0^{}') -Mode FinalizeExisting -ReleaseId 1204616 -ArtifactDirectory $originalFiles
# 仅在上一条成功后继续；不再调用构建脚本或普通 Publish 模式。
./eng/Publish-GitHubRelease.ps1 -Tag v0.2.0 -Repository warren-cao/AutoFocusFactoryTool -GiteeRepository warren-cao/auto-focus-factory-tool -ArtifactDirectory $originalFiles
```

收尾先校验原始四个文件的签名和哈希，再核对指定 Release ID、标签、四个远端附件的名字/长度，并匿名下载所有远端附件流式比对 SHA-256。只有完全一致才补齐必填元数据、将同一 Release 转正式版；不创建 Release，不重新上传或替换附件。若它已是正式版，则仅复核、不修改。网络异常、内容不符或 ID 不符均停止；未增加自动重试。

## 10. 源码与发布仓分离、GitHub 转私有

- **GitHub：**保存源码、版本标签、更新公钥、工作流和变更记录；私钥、Gitee Token 仍只放 Actions Secrets。私有仓内 `github.token` 仍可用于该仓库的受权发布，runner 仍需有仓库访问权。
- **Gitee：**必须公开，只存 README/发布说明及 Release 四个二进制制品；Gitee 自动生成的“源码 ZIP”应只有该发布仓的说明文件，不包含上位机源码。
- **客户端：**继续匿名查询 Gitee Release，验签和校验 SHA-256；不保存 GitHub/Gitee 凭据，不因 GitHub 私有化而改变升级流程。
- **旧源码历史：**本次只是解除发布流程的源码耦合，**不会删除 Gitee 已经存在的代码、分支、标签或历史**。直接提交一次“删除所有文件”也不能隐藏 Git 历史；GitHub 转私有同样无法收回此前别人下载或 fork 的公开代码。

真正清除 Gitee 已有源码有两种迁移方式，需要维护者另行确认：

1. **保留现有仓库地址：**先备份 Release 四个原始制品和元数据，再按平台能力单独规划清理所有源码引用/历史，仅保留 README 锚点；旧版本标签、源码压缩包和平台保留数据也需检查。不能直接清空整个仓库，避免连同升级 Release 一起删除。
2. **新建公开发布仓：**只初始化 README，再迁移验证过的签名制品；需要更新客户端的 Gitee 查询配置、下载地址和签名清单，并通过原升级源发布迁移桥接版本。**仅修改流水线变量不能迁移已安装客户端。**不要把旧签名清单里的下载地址直接改写，改写会使签名失效。

已选择第 2 种方式，新仓名称确定为 `warren-cao/focusir-workbench-releases`。没有删除旧仓或源码历史，也没有替你修改 GitHub 仓库可见性。发布前应确认公开包不含源码、私钥或敏感配置；.NET 编译产物本身仍可能被反编译，源码仓私有化并不是二进制防逆向机制。

## 11. v0.2.1 桥接迁移的执行顺序

1. 在 Gitee 网页创建公开仓 `warren-cao/focusir-workbench-releases`，只初始化 README，完成平台要求的公开审核，并确认匿名 API 可访问。不要将源码推过去。本机 Git 登录不是 Gitee API Token，无法代替建仓授权。
2. 确认现有 `GITEE_TOKEN` 对新、旧两个仓库均有发布权限；`GITEE_RELEASE_TARGET` 留空即可使用新仓默认分支，旧仓可用独立变量 `GITEE_BRIDGE_TARGET`，默认也取其默认分支。
3. 本次代码已准备 `VersionPrefix=0.2.1` 和变更记录，并把程序默认查询源及“关于”地址改为新仓；**还没有创建或推送 `v0.2.1` 标签**。
4. 源码审查及人工验收后，仅在 GitHub 创建 `v0.2.1` 标签。当前两条构建工作流只在此标签上启用 `GITEE_BRIDGE_REPOSITORY=warren-cao/auto-focus-factory-tool`；后续版本只发布新仓。
5. 构建脚本校验“源码默认升级地址 = 主发布仓”，生成主发布四个文件以及 `artifacts/bridge-release/` 四个桥接文件。ZIP 逐字节相同；桥接清单的包地址仍指向旧仓，必须使用同一受信密钥重新签名，不能直接编辑原清单。
6. 两套签名制品都先归档。正式发布顺序固定为 **新 Gitee 正式版 → GitHub 同一主制品备份 → 旧 Gitee 桥接正式版**。旧客户端只有在新源已准备好后才会看到桥接更新。
7. 旧 `v0.2.0` 客户端从旧仓获得 `v0.2.1`，下载旧仓中的桥接清单及同一 ZIP，正常验签、安装和健康检查；升级后的程序改为查询新仓。新安装的 `v0.2.1` 也直接查询新仓。
8. 下一版本（如 `v0.2.2`）只发新仓。逐工位确认“关于”地址和升级查询正常后，再单独决定旧仓的可见性或退役。**不要在迁移完成前删除、私有化旧仓或删除桥接包。**

如桥接阶段失败，使用收尾工作流选择原构建 run、旧仓及 `artifact_kind=bridge`，它会使用 `focusir[-self-hosted]-bridge-...` 制品，只收尾旧仓、不向已有 GitHub 同版本主 Release 再上传另一份清单。主发布收尾则选择新仓及 `artifact_kind=release`。

如果主发布/GitHub 备份失败导致旧桥接步骤根本没有执行，先收尾主发布，再用同一原构建 run、旧仓、`artifact_kind=bridge` 和 `gitee_release_id=0` 补发缺失的桥接 Release。只有桥接模式允许 ID=0；它仍拒绝覆盖同名 Release，并提前检查新源和 GitHub 主备份都已正式发布。旧桥接已有 Release 时必须填其真实 ID，不能用 0。

## 官方参考

- [GitHub Windows 托管节点](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
- [GitHub Actions Secrets](https://docs.github.com/en/actions/security-for-github-actions/security-guides/using-secrets-in-github-actions)
- [自托管 runner 标签与调度](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/use-in-a-workflow)
- [自托管 runner 安全要求](https://docs.github.com/en/actions/reference/security/secure-use#hardening-for-self-hosted-runners)
- [Gitee Release 和附件 API](https://gitee.com/sdk/gitee5j/blob/main/docs/RepositoriesApi.md)
