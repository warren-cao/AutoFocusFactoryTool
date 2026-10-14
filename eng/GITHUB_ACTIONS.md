# GitHub Actions 构建与发布

当前推荐方案：GitHub 云端 Windows 构建，向 Gitee 发布正式升级包，并把相同的四个文件发布到 GitHub Releases。无需 Gitee 企业版或自有构建主机。

- GitHub 仓库：<https://github.com/warren-cao/AutoFocusFactoryTool>
- 工作流：`.github/workflows/release.yml`
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

## 4. 工作流执行内容

1. 校验严格的三段式稳定标签，以及手动运行的工作流分支。
2. 完整检出标签与所有历史，确认提交属于 `origin/master`。
3. 在 `windows-2025` 安装符合 `global.json` 的 .NET 10 SDK，按锁文件缓存 NuGet 包。
4. 正式发布模式检查 Gitee 源码提交已存在、目标 Release 未发布。
5. 调用 `eng/release.ps1`，执行锁定还原、Release 警告即错误编译、现有纯软件测试、两程序自包含发布、ZIP 打包与 ECDSA 签名。
6. 上传诊断制品（14 天）和签名升级包制品（30 天），仅收集指定目录，不上传私钥。
7. 调用 `eng/Publish-GiteeRelease.ps1`，复核四个文件的签名和摘要，先创建预发布 Release，再逐个上传 ZIP、签名、摘要和清单；确认附件列表完整后转为正式版。
8. 使用 GitHub CLI 把相同四个文件发布为 GitHub 正式 Release。

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

## 官方参考

- [GitHub Windows 托管节点](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
- [GitHub Actions Secrets](https://docs.github.com/en/actions/security-for-github-actions/security-guides/using-secrets-in-github-actions)
- [Gitee Release 和附件 API](https://gitee.com/sdk/gitee5j/blob/main/docs/RepositoriesApi.md)
