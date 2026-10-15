# 标签自动发布与静态升级入口

唯一工作流为 **FocusIR Release (Self-hosted)**，在 `DESKTOP-AI3E9IB` 上运行。向 GitHub 推送稳定版 `vX.Y.Z` 标签即自动发布；普通源码提交不会发版。工作流也保留手动派发入口，用于尚未发布版本的诊断或构建。

GitHub 保存源码与源码标签；公开 Gitee 仓库 `warren-cao/focusir-workbench-releases` 保存二进制 Release、静态升级清单和历史说明，不同步应用源码。正式发布顺序为 Gitee → GitHub → 静态升级入口。

## 一次性配置

在 GitHub 仓库 Settings → Secrets and variables → Actions 配置：

| 类型 | 名称 | 用途 |
| --- | --- | --- |
| Secret | `UPDATE_SIGNING_KEY_PKCS8` | 与 `eng/keys/focusir-release-2026.pem` 配对的现有 ECDSA P-256 私钥，PKCS#8 Base64 文本 |
| Secret | `GITEE_TOKEN` | 仓库所有者账号的私人令牌；需有新发布仓的 Release/附件权限，以及 HTTPS Git 代码读写权限 |
| Variable，可选 | `GITEE_RELEASE_TARGET` | Gitee 发布仓已有分支或提交，留空取默认分支，仅用于 Release 标签锚点 |

Gitee 发布仓必须公开且已有 `master` 分支，至少包含一个 README 初始化提交。静态入口固定读取 `master/latest/`，该分支须允许发布账号普通推送。`GITEE_RELEASE_TARGET` 不能填写 GitHub 源码 SHA；它也不改变静态入口所在分支。

GitHub Release 使用内置 `github.token`，发布 job 已有 `contents: write`，无需额外 GitHub PAT。Gitee 预检查、构建、附件上传和静态入口更新均分别注入 `GITEE_TOKEN`，步骤级环境不会自动继承。签名私钥不能替代 Gitee 令牌；客户端不包含任何私人令牌。

runner 名称需为 `DESKTOP-AI3E9IB`，注册标签为 `self-hosted`、`Windows`、`X64`；账号 PATH 包含 Git 和 PowerShell 7.4+。SDK 按 `global.json` 安装到临时目录，构建主机需能访问 GitHub、微软 SDK 下载站、NuGet 和 Gitee。采用独立构建目录，不在 Actions checkout 目录保留日常开发文件或私钥。

## 日常发布

首次使用前，将本次工作流和工具修改提交并推送到 `master`。之后每次完成代码修改、检查和人工验收后，正常提交并推送，然后只创建一次新的稳定版本标签，例如：

```powershell
git push origin master
git tag -a v0.3.2 -m "修复更新检查，改为静态签名入口和标签自动发布"
git push origin v0.3.2
```

推送标签后，Actions 自动运行自托管发布，不需要再勾选 `publish`。版本必须高于 Gitee 已有稳定版，且标签提交属于 `origin/master` 历史。不要移动已发布标签或预先创建同名 Release，逐版确认成功后再发下一版。

**发版无需修改 `Directory.Build.props`、`packages.lock.json` 或 `CHANGELOG.md`。**应用、更新器、程序集及清单版本均从标签注入。依赖还原使用源码中的固定开发默认版本，保持锁文件稳定；只有修改项目依赖时才重新生成并提交受影响的锁文件。`CHANGELOG.md` 保留历史记录，不再是发布必填项。

## 自动发布说明

脚本选择当前标签历史中版本号最高、且低于当前版本的稳定标签作为对比起点，按时间顺序收集两个标签之间的非合并提交说明，附加源码提交 SHA 和当前附注标签的说明。首次标签使用其全部提交；同提交的新标签会记录没有新增源码提交。

改动项来自 Git，因此提交说明应清楚表达用户可见变化；附注标签的 `-m` 可补充本版总述。每次发布自动保存：

- `update-manifest.json` 的 `releaseNotes`：经过数字签名，上位机显示这里的内容。
- 安装 ZIP 根目录的 `release-notes.md`：随安装包保存。
- 公开发布仓的 `notes/vX.Y.Z.md`：按标签归档，已有同标签文件不覆盖。
- Gitee 和 GitHub Release 正文：与清单里的说明一致。

## 工作流执行

工作流校验稳定标签和 runner，检出完整历史并确认标签属于 `master`，再安装 SDK 和加载机密。`ci.ps1`、`release.ps1` 及两个上传脚本固定取自本次 `github.workflow_sha`；应用源码、公钥和提交记录来自选中的标签。

构建执行已有的锁定还原、警告即错误编译和纯软件 CI，生成自包含 ZIP、原始 UTF-8 清单、ECDSA P-256 签名和 SHA-256 文件。Release 附件仍为四件套：

```text
FocusIR.Workbench-vX.Y.Z-win-x64.zip
update-manifest.json
update-manifest.sig
SHA256SUMS.txt
```

Gitee 上传时保持预发布，四件套完整后转正式版；随后 GitHub 创建草稿、上传相同四件套并转正式版。两者成功后执行 `Publish-GiteeRelease.ps1 -Mode PublishFeed`：复核正式 Gitee 附件和原始签名文件，将清单、签名及历史说明在同一次 Git 提交中推送到发布仓 `master`，最后匿名回读固定文件比对字节。

Git 推送只允许普通快进，不强制覆盖。凭据仅进入 Git 子进程环境，不写入 URL、命令行、Git 配置文件或日志；临时克隆只存放公开发布仓的内容，不影响应用源码。入口拒绝版本回退及同版本制品替换。

## 客户端升级

新客户端固定读取：

```text
https://gitee.com/warren-cao/focusir-workbench-releases/raw/master/latest/update-manifest.json
https://gitee.com/warren-cao/focusir-workbench-releases/raw/master/latest/update-manifest.sig
```

不再调用匿名 Release API。客户端验签后比较稳定版本，包地址必须指向当前仓对应标签的 Release 附件，下载时仍核对包大小及 SHA-256。两次读取恰逢入口更新而不匹配时，只有限重读一次，不绕过验签。入口尚未初始化返回没有可用稳定版。

Gitee 的原始文件入口可能跳转到 `raw.giteeusercontent.com`。客户端与发布回读均只在静态清单/签名请求中允许这个精确 HTTPS 主机；安装包仍只允许 Gitee 的发布下载主机，不放开任意子域名。回读日志记录跳转前后主机、HTTP 状态、长度及摘要，不记录带临时签名的完整跳转 URL。

`v0.3.1` 及更早的客户端仍查询 API，无法仅靠更新服务器文件改变自身代码。受 403 限流影响的工位需先从 Release 页面人工安装一次新客户端，之后使用固定入口。安装步骤见 [安装与验收](CICD_GUIDE.md)。

## 手动运行与故障处理

手动构建仍从 Actions → **FocusIR Release (Self-hosted)** → **Run workflow**，分支选 `master`，填写已有标签。`publish=false` 只读诊断、构建、签名和归档；`publish=true` 发布尚未发布的版本。两种模式均需要相应 Secrets，仍拒绝重复版本。

修复发布工具后，应从当前 `master` 新建运行，不只重跑旧记录。正式 Release 已存在时不要重跑完整发布覆盖；Actions 的原始四件套保留 30 天，诊断保留 14 天。

- Gitee API 401/403：检查该步骤的令牌注入、有效性、权限及限流，签名私钥与 HTTP 访问无关。
- Gitee 成功、GitHub 失败：下载原始四件套，通过 `Publish-GitHubRelease.ps1` 补发；有草稿时显式指定 `-ExistingReleaseId`。
- Gitee 四个附件完整但仍预发布：使用原始四件套执行 `VerifyExisting` 和 `FinalizeExisting`，不重新构建或签名。
- 两个平台成功、入口更新失败：检查令牌的 Git 代码读写权限、master 推送权限、网络和缓存。单独重试 `PublishFeed`，不重建已发布版本。
- 已出现 `feed-pushed` 后回读失败：先查看 `remote-artifact-redirect` 及失败日志的 `stage`、`hostName`、`statusCode`、长度和摘要。`UntrustedRedirect` 是主机白名单问题，不能当作缓存错误处理。旧 `v0.3.2` 客户端未允许原始文件 CDN，需安装后续包含修复的版本。

原始四件套放入 `artifacts/recovery/` 后，在已准备受控发布凭据的环境执行，例如恢复已完成正式发布的 `v0.3.2` 静态入口：

```powershell
$releaseTag = 'v0.3.2'
$sourceCommit = git rev-parse "$releaseTag^{}"
./eng/Publish-GiteeRelease.ps1 -Tag $releaseTag -SourceCommit $sourceCommit -Mode PublishFeed -ArtifactDirectory ./artifacts/recovery
```

这也可用于初始化已正式发布版本的静态入口。公钥需与原始清单一致；若发生密钥轮换，传入保留原公钥的源码检出目录 `-SourceDirectory`。首次开启新入口无需重新生成私钥。上传日志位于 `artifacts/release-logs/`；不会自动删除、移动标签或覆盖已发布附件。
