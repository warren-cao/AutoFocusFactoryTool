# GitHub Actions 自托管发布

当前唯一发布入口是 **FocusIR Release (Self-hosted)**，文件为 `.github/workflows/release-self-hosted.yml`。工作流仅手动运行；提交代码和推送标签不会自动发布。

GitHub 仓库 `warren-cao/AutoFocusFactoryTool` 保存源码、master 和源码版本标签。公开 Gitee 仓库 `warren-cao/focusir-workbench-releases` 只保存发布说明和二进制 Release。每次正式发布先上传 Gitee，再将完全相同的四个附件上传到 GitHub Releases。客户端仅匿名查询 Gitee，不使用 GitHub 作为升级回退源。

## 一次性配置

GitHub 仓库 Settings → Secrets and variables → Actions 中保留：

| 类型 | 名称 | 用途 |
| --- | --- | --- |
| Secret | `UPDATE_SIGNING_KEY_PKCS8` | 与 `eng/keys/focusir-release-2026.pem` 配对的现有 ECDSA P-256 私钥，PKCS#8 Base64 文本 |
| Secret | `GITEE_TOKEN` | 对 `warren-cao/focusir-workbench-releases` 有 Release 和附件发布权限的 Gitee 令牌 |
| Variable，可选 | `GITEE_RELEASE_TARGET` | Gitee 发布仓已有的分支或提交；留空自动使用其默认分支 |

GitHub Release 使用工作流内置的 `github.token`，发布 job 已声明 `contents: write`，无需额外 GitHub PAT。`GITEE_RELEASE_TARGET` 不能填写 GitHub 源码 SHA：两个仓库内容不同，Gitee 标签由发布 API 在其 README 锚点上创建，不需要手动同步源码或标签。

Gitee 发布仓必须公开，并已有 README 初始化提交。可使用 `eng/release-repository/README.md` 作为说明。客户端不保存 Gitee 令牌。

自托管 runner 要求：

- 名称为 `DESKTOP-AI3E9IB`，注册标签为 `self-hosted`、`Windows`、`X64`，运行时在线。
- runner 账号 PATH 包含 Git、PowerShell 7.4+（`pwsh`）；安装后重启 runner 进程或服务。
- 能访问 GitHub、GitHub Artifacts、微软 SDK 下载站、NuGet 和 Gitee；工作流按 `global.json` 安装 .NET SDK 到 runner 临时目录。
- 使用独立构建目录。不要把日常开发文件或私钥放入 Actions checkout 目录，checkout 会清理该目录。

源码仓可以私有，Gitee 公开升级不受影响。持久 runner 不接收外部 PR 的签名发布任务，不连接设备或自动驱动电机。

## 提交代码、创建标签

以准备下一版 `0.2.2` 为例；实际版本必须高于 Gitee 已有稳定版，且标签未使用过。

1. 在 `master` 完成代码修改与人工验收。
2. 将 `Directory.Build.props` 的 `VersionPrefix` 改为 `0.2.2`，在 `CHANGELOG.md` 增加 `## [0.2.2] - YYYY-MM-DD` 及本版内容。
3. 更新并检查锁文件，再提交源码、版本、更新日志和受影响的 `packages.lock.json`：

```powershell
Set-Location F:\Project\github-AutoFocusFactoryTool
git switch master
git pull --ff-only origin master
dotnet restore .\FocusIR.Workbench.sln --force-evaluate
git diff
git add -u
git commit -m "Release v0.2.2"
git push origin master
git tag -a v0.2.2 -m "FocusIR Workbench v0.2.2"
git push origin v0.2.2
```

提交前核对 `git status`，新建源码文件也要显式加入提交；`git add -u` 只暂存已跟踪文件的变化。若功能代码已经提交，发布提交只需包含版本、更新日志与锁文件。没有依赖变化也需检查锁文件，因为项目引用版本随 `VersionPrefix` 改变。

标签必须指向已推送到 `master` 历史中的发布提交。不要移动、覆盖或删除已发布标签，也不要预先创建同名 Gitee Release。首次使用此简化流程前，须先将清理后的工作流和脚本提交到 GitHub `master`。

## 手动完成发布

1. 打开 GitHub 源码仓 Actions → **FocusIR Release (Self-hosted)** → **Run workflow**。
2. 分支选择 `master`，`tag` 填 `v0.2.2`。
3. 勾选 `publish`，即 `publish=true`，再点击 **Run workflow**。
4. 等待自托管主机完成，并确认 Gitee 和 GitHub 同名 Release 均为正式版。

`publish` 默认不勾选。`publish=false` 只执行只读诊断、构建、现有纯软件测试、签名和制品归档，不创建远端 Release；仍需要签名 Secret，且仍检查版本递增及版本是否重复。普通发版可直接选择 `publish=true`，不必先运行一遍只构建模式。

已安装 GitHub CLI 时，也可手动派发：

```powershell
gh workflow run release-self-hosted.yml --repo warren-cao/AutoFocusFactoryTool --ref master -f tag=v0.2.2 -f publish=true
```

发布串行执行，不取消上传中的任务。逐版确认完成后再发布下一版。

## 执行内容与升级查询

工作流验证主分支、稳定标签和 runner 名称，检出源码标签与完整历史，确认标签提交属于 `origin/master`。发布工具从本次工作流提交 `github.workflow_sha` 加载到 runner 临时目录；应用源码、版本、更新日志、公钥仍来自选中的标签。

`eng/release.ps1` 检查干净工作区、标签/版本/更新日志一致、客户端默认升级仓与发布仓一致、版本递增及目标版本未发布，再调用现有 `eng/ci.ps1`，完成锁定还原、警告即错误编译、现有纯软件测试、自包含打包和签名。

Gitee 预检查、构建签名和 Gitee 上传步骤均须分别注入 `GITEE_TOKEN`。构建脚本需要该令牌查询已有版本，并使用与上传脚本相同的 Bearer 认证。步骤级环境变量不会自动传递到后续步骤；`UPDATE_SIGNING_KEY_PKCS8` 只用于清单签名，不能代替 Gitee 令牌。

正式发布的四个文件为：

```text
FocusIR.Workbench-v0.2.2-win-x64.zip
update-manifest.json
update-manifest.sig
SHA256SUMS.txt
```

Gitee 上传期间保持预发布，附件完整后转正式版；随后 GitHub 创建草稿、上传同一套文件并转正式版。两个平台依次执行，因此若第二步失败，Gitee 可能已经发布成功。

客户端默认查询：

```text
https://gitee.com/api/v5/repos/warren-cao/focusir-workbench-releases/releases/latest
```

仅接受高于当前版本的严格 `vX.Y.Z` 正式版本，读取该 Release 的附件，校验清单 ECDSA 签名、版本、包大小及 SHA-256 后下载。包地址由签名清单提供，正常发布时指向同一 Gitee 仓库。没有旧仓查询或回退分支。

## 制品与故障处理

Actions 保存签名四件套 `focusir-self-hosted-release-<tag>-<attempt>` 30 天，诊断制品保存 14 天。上传诊断位于 `artifacts/release-logs/`，包括请求状态、附件大小、传输进度及错误分类，不记录令牌、私钥或原始响应正文。传输进度不代表服务端已完整保存附件。

- 缺少 Secret、runner 离线、PowerShell 未加入 PATH：修复对应配置；尚未创建 Release 时，可重新手动运行。
- Gitee 401/403：检查对应步骤是否注入 `GITEE_TOKEN`、令牌权限及限流；前一个预检查成功不代表后续构建步骤已取得令牌。不要更换签名私钥来处理 HTTP 权限错误。
- Gitee 无发布锚点：创建 README 初始化提交，核对默认分支或 `GITEE_RELEASE_TARGET`。
- 同名 Release 已存在：不要重跑完整发布覆盖，也不要移动源码标签。检查实际状态和原始附件。
- Gitee 已正式发布、GitHub 失败：保留 Gitee Release，从原运行下载四件套，使用 `Publish-GitHubRelease.ps1` 补发；已有 GitHub 草稿时显式传入其 `-ExistingReleaseId`。
- Gitee 四个附件完整但仍为预发布：使用原运行的四件套，通过下述模式核对、完成该 Release。不得重新编译、重新签名或替换附件。

删除专用收尾工作流后，上传脚本仍保留适用于当前发布仓的原制品恢复能力。以下命令供维护者处理失败发布时使用；执行前准备受控环境中的发布凭据，不将凭据写入命令、源码或日志：

```powershell
# 在仓库根目录执行，原始四件套放入 artifacts/recovery。
# 此示例仅适用于处理失败的 v0.2.2；不要拿其他版本的文件替代。
$releaseTag = 'v0.2.2'
$sourceCommit = git rev-parse "$releaseTag^{}"
$originalFiles = Join-Path $PWD 'artifacts/recovery'
./eng/Publish-GiteeRelease.ps1 -Tag $releaseTag -Mode Validate -ArtifactDirectory $originalFiles
# 将 <ReleaseID> 替换为已核对的 Gitee 正整数 Release ID。
./eng/Publish-GiteeRelease.ps1 -Tag $releaseTag -SourceCommit $sourceCommit -Mode VerifyExisting -ReleaseId <ReleaseID> -ArtifactDirectory $originalFiles
./eng/Publish-GiteeRelease.ps1 -Tag $releaseTag -SourceCommit $sourceCommit -Mode FinalizeExisting -ReleaseId <ReleaseID> -ArtifactDirectory $originalFiles
# 仅当 GitHub 尚无同名 Release 时；若已有草稿，追加 -ExistingReleaseId <草稿ID>。
./eng/Publish-GitHubRelease.ps1 -Tag $releaseTag -Repository warren-cao/AutoFocusFactoryTool -ArtifactDirectory $originalFiles
```

`Validate` 离线校验签名、结构及摘要；`VerifyExisting` 匿名比对远端附件字节与摘要；`FinalizeExisting` 完成同一个 Gitee Release。所用公钥需与原始清单的 `keyId` 一致；若其后发生密钥轮换，使用原源码标签的检出目录作为 `-SourceDirectory`。远端缺少附件或摘要冲突会中止，脚本不会自动删除或覆盖远端内容。无法恢复的版本由人工核对后处理，正常修复发布更高版本。

首次安装、设备人工验收见 [安装与验收](CICD_GUIDE.md)。
