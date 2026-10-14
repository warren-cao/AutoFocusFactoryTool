# 安装与验收

构建发布统一使用 GitHub 的 **FocusIR Release (Self-hosted)**，提交代码、创建标签、配置凭据与手动发布步骤见 [GitHub Actions 发布指南](GITHUB_ACTIONS.md)。

## 首次安装

从 [Gitee 正式 Release](https://gitee.com/warren-cao/focusir-workbench-releases/releases) 下载实际发布的 `FocusIR.Workbench-vX.Y.Z-win-x64.zip` 和 `SHA256SUMS.txt`。不要下载发布仓自动生成的源码压缩包。

核对 ZIP 的 SHA-256：

```powershell
Get-FileHash -LiteralPath <ZIP绝对路径> -Algorithm SHA256
```

确认与 `SHA256SUMS.txt` 对应条目一致，保留原 ZIP，将 ZIP 解压到临时目录，再在解压目录执行：

```powershell
.\launcher\FocusIR.Workbench.Updater.exe --install-package <ZIP绝对路径> --version <X.Y.Z> --sha256 <包的64位SHA256>
```

将占位符替换为所下载版本的实际值。更新器安装到当前用户的 `%LocalAppData%\ThermEye\FocusIRWorkbench` 并创建开始菜单入口，不需要管理员权限。正常启动使用开始菜单入口；开发目录直接运行不等于完成受管安装。

## 后续升级

已安装版本通过“帮助 → 检查更新”匿名访问新 Gitee 仓库的最新正式 Release。客户端验证升级清单签名、版本、包大小和 SHA-256。设备须断开，活动任务、控制租约及未完成编辑/保存须结束，用户确认后才开始安装。

更新器使用独立版本目录，切换版本后等待新程序报告健康状态，失败则自动回滚到上一版本。升级过程不会主动连接设备或发送电机命令。签名或摘要校验失败时停止安装。

## 人工验收

本地源码修改按项目约定只检查源码和编译，不新增、生成或运行测试程序。发布工作流保留已有纯软件 CI；设备联调和实际镜头测试始终由人工完成。

在真实设备上确认连接与断开、能力读取、配置管理、聚焦/标定任务、曲线编辑和保存符合现场要求；在已受管安装的工位人工检查新版本发现、下载、安装与启动。编译成功不代表镜头行程、光学效果或动态联动已通过验收。
