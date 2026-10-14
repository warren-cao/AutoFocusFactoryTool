# GitHub Actions 构建与发布

支持两条发布路径：GitHub 云端 Windows 节点，或自托管 Windows 节点 `DESKTOP-AI3E9IB`。两者均向 Gitee 发布正式升级包，并把相同的四个文件发布到 GitHub Releases，无需 Gitee 企业版。

- GitHub 仓库：<https://github.com/warren-cao/AutoFocusFactoryTool>
- 云端工作流：`.github/workflows/release.yml`
- 自托管工作流：`.github/workflows/release-self-hosted.yml`（仅手动触发）
- 上位机升级源：<https://gitee.com/warren-cao/auto-focus-factory-tool/releases>

## 1. 一次性配置 Secrets

进入 GitHub 仓库 → Settings → Secrets and variables → Actions → New repository secret。

| Secret | 内容 |
| --- | --- |
| `UPDATE_SIGNING_KEY_PKCS8` | 现有 `artifacts/signing/update-signing-key.pkcs8.b64` 的完整文本，须与 `eng/keys/focusir-release-2026.pem` 配对 |
| `GITEE_TOKEN` | Gitee 私人令牌，令牌账号须具有目标仓库的源码读取、Release 创建/修改和附件上传权限 |

Gitee 令牌在 <https://gitee.com/profile/personal_access_tokens> 创建。不要用 GitHub Token 代替。私钥不需要重新生成，也不要提交到仓库；只在平台的机密字段粘贴其值。

工作流将 GitHub Token 限制在发布 job 的 `contents: write`；使用内置 `github.token` 发布 GitHub Release，无需额外 GitHub PAT。第三方 Action 实际只使用 GitHub 官方 actions，并固定到已核对的完整提交 SHA。

## 2. 准备源码和版本标签

GitHub 构建的提交必须已经同步到 Gitee；Gitee Release 需要关联实际存在的源码提交。工作流会提前通过 Gitee API 核对，缺失时立即停止。工作流不会自动改写 Gitee 的 master。

以首版 `v0.2.0` 为例，先确认本次工作流提交已在两个仓库，版本文件为 `0.2.0` 且更新日志有 `[0.2.0]`，随后运行：

```powershell
Set-Location F:\Project\auto-focus-factory-tool
git status --short
git push origin master
git push github master
git tag -a v0.2.0 -m "FocusIR Workbench v0.2.0"
git push origin v0.2.0
git push github v0.2.0
```

最后一条命令触发 GitHub Actions。必须先推送 Gitee，后推送 GitHub 的标签。不要先创建同名 Gitee Release。

本机此前已确认 GitHub 连接需要 HTTP/1.1 时，可把相关 push 改为：

```powershell
git -c http.version=HTTP/1.1 -c http.sslBackend=schannel push github master
git -c http.version=HTTP/1.1 -c http.sslBackend=schannel push github v0.2.0
```

后续版本先更新 `Directory.Build.props`、`CHANGELOG.md`，执行 `dotnet restore .\FocusIR.Workbench.sln --force-evaluate` 并检查提交相应锁文件，然后重复同步和标签流程。已发布标签不能重新指向另一提交。

## 3. 手动运行与只构建模式

进入 Actions → FocusIR Release → Run workflow：

1. 工作流分支选择 `master`。
2. `tag` 填写已推送到 GitHub 的完整版本标签，例如 `v0.2.0`，不能填写 master、提交 SHA 或预发布标签。
3. `publish=true`：完整发布到 Gitee 和 GitHub。
4. `publish=false`：执行测试、构建、签名和成品检查，上传 Actions Artifacts，不创建任何远端 Release；仍需要签名 Secret。

只构建模式仍保留现有发布脚本的版本递增、远端重复版本检查，因此不能用于覆盖已发布版本。测试新工作流可使用尚未发布的版本标签。

构建脚本及两个平台的上传脚本均固定取自本次工作流的提交（`github.workflow_sha`），再通过 `-SourceDirectory` 对选中的版本标签源码构建和验签。工具位于 runner 临时目录，不污染源码工作区。这样可以在 master 修复 CI 工具后，手动发布尚未成功发布的旧标签，而不移动标签、改变应用源码或跳过安全检查。版本号、更新日志、验签公钥与编译内容仍全部来自所选标签。

如果旧构建因 `tag_name` 属性错误失败，修复推送后不要只点击旧记录的 Re-run；应在 Actions → FocusIR Release → Run workflow 选择 `master`，tag 仍填 `v0.2.0`，使用包含修复的新工作流。仅在该版本尚未发布时重试；Gitee 与 GitHub 的标签仍须指向相同源码提交。

## 4. 工作流执行内容

1. 校验严格的三段式稳定标签，以及手动运行的工作流分支。
2. 完整检出标签与所有历史，确认提交属于 `origin/master`。
3. 在 `windows-2025` 安装符合 `global.json` 的 .NET 10 SDK，按锁文件缓存 NuGet 包。
4. 正式发布模式检查 Gitee 源码提交已存在、目标 Release 未发布。
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

上位机仍从 Gitee 查询和下载升级。GitHub Release 作为同一产物的额外下载入口，不会自动改变客户端更新地址。

Actions 页面始终尝试归档测试结果和 MSBuild 日志；构建、测试、签名或成品校验失败时，不会进入发版步骤。首版工位安装和真实设备人工验收步骤见 [CI/CD 执行指南](CICD_GUIDE.md)。

## 6. 重试与故障处理

- **缺少 Secret：**完成第 1 节配置后重新运行；不要把私钥或令牌写入日志排查。
- **Gitee 无对应源码：**先向 Gitee 推送同一提交和标签，再重新运行。
- **Gitee 返回 401/403：**核对令牌有效性、目标仓库和 Release/附件权限；公开仓库能读不代表令牌有写权限。
- **Gitee 上传中断：**已创建的版本保持预发布；不要启用原 Gitee Go 发布任务并行覆盖。先检查残留版本和附件，确认未作为正式版分发，再人工移除本次不完整预发布记录，或修复后发布更高版本。脚本不会自动删除远端记录。
- **Gitee 成功、GitHub 镜像失败：**Gitee 已是有效发布，不能重跑整条流程来覆盖它。下载这次 Actions 的签名制品，使用 `gh release create` 手动补发同一 GitHub 标签及四个文件。不要重新编译同一版本来替换原产物。
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
3. 分支选择 `master`，tag 填 `v0.2.0`，保持 **publish=false**。
4. 查看 PowerShell 前置检查和实际 runner 名称，再查看 Gitee 只读诊断、编译、既有软件测试和签名打包结果。
5. 下载 `focusir-self-hosted-release-...` 和 `focusir-self-hosted-diagnostics-...` 制品。此模式不会创建、上传、修改或删除远端 Release；Gitee 已有不完整预发布时仍可以进行只读诊断。它验证基本 API 连通性，不验证大 ZIP 上传。

正式发布时，再手动运行同一工作流并选择 **publish=true**，执行完整 Gitee 和 GitHub 发布。

**已有预发布不能直接重发：**之前失败留下的 `v0.2.0` 预发布（Release `1204184`）必须先由维护者检查是否仍为预发布、附件是否完整、是否曾被分发。确认可清理后人工删除该不完整 Release 记录（保留原标签），或准备更高的新版本。脚本不会自动删除、覆盖或续传现有 Release；不要绕过这个检查。对已正式分发的版本，必须发布更高版本。

云端和自托管工作流共用 `focusir-stable-release` 并发锁。标签 push 仍触发原云端流程；自托管方案仅手动触发。新版本如需只在自托管构建，先推送并等待自动云端流程结束，或事先在 Actions 页面停用云端工作流；不要让两个流程争相发布同一标签。

## 8. 查看 Gitee 上传诊断

`eng/Publish-GiteeRelease.ps1` 的 `CheckRemote`、`Diagnose`、`Publish` 模式都会在 `artifacts/release-logs/gitee-*.jsonl` 留下结构化日志，同时显示在 Actions 输出中。两个工作流都在最后以 `always()` 收集这些日志，所以上传失败时仍能下载。

- `session`：模式、标签、runner 名称、PowerShell 版本、请求超时，以及是否设置了代理环境变量。仅记录代理是否配置，不记录代理地址；未设置环境变量不代表系统没有代理。
- `request-start`：请求 ID、HTTP 方法、无查询参数的 API 路径、附件名称、文件字节数和请求长度。
- `upload-progress`：每约 15 秒记录已写入传输流的字节数和耗时。`waitingForResponse=true` 表示文件字节已经写入流，**不代表服务端已完整接收、持久化或创建附件**。
- `request-response`：HTTP 状态码、总耗时和上传计数。401/403 优先核对凭证及权限，413 核对体积限制，429 核对频率限制，5xx 核对服务端情况。
- `request-error`：超时/取消或传输错误分类、异常链类型、HRESULT，以及可用的 `HttpRequestError`、Socket 错误码；不输出原始异常消息、堆栈、请求头、正文、Token、私钥或代理凭据。
- `response-parse-error`：服务端返回了无法解析的 JSON，保留请求 ID，但不输出原始正文。
- `existing-release`：只读诊断查询到的已有 Release ID、预发布状态和附件数量。

默认每个 HTTP 请求仍为 **15 分钟**，可通过 `-RequestTimeoutMinutes` 显式调整（1–120 分钟）；只延长超时不是链路问题的修复。发生超时时先核对附件实际状态，再决定人工处理，不自动重试可能产生重复附件的 POST。

只读诊断命令（需要本地进程已安全设置 `GITEE_TOKEN`，不要把其值写进命令历史）：

```powershell
./eng/Publish-GiteeRelease.ps1 -Tag v0.2.0 -TargetCommit (git rev-parse 'v0.2.0^{}') -Mode Diagnose
```

## 官方参考

- [GitHub Windows 托管节点](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
- [GitHub Actions Secrets](https://docs.github.com/en/actions/security-for-github-actions/security-guides/using-secrets-in-github-actions)
- [自托管 runner 标签与调度](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/use-in-a-workflow)
- [自托管 runner 安全要求](https://docs.github.com/en/actions/reference/security/secure-use#hardening-for-self-hosted-runners)
- [Gitee Release 和附件 API](https://gitee.com/sdk/gitee5j/blob/main/docs/RepositoriesApi.md)
