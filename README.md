# FocusIR 工厂自动聚焦工作台

本仓库是 Windows 工厂工作台与设备端 FocusIR 模块配套使用的上位机工程。工作台负责连接设备、读取能力和状态、管理镜头型号与物距配置、发起聚焦和标定任务、展示采样与曲线、提交人工曲线编辑。电机控制、清晰度采样、模型保存和实时变倍追焦在设备端执行。

## 目录与版本

| 路径 | 内容 |
| --- | --- |
| `FocusIR.Workbench.sln` | .NET 10 WPF 工作台解决方案 |
| `tools/FocusIR.Workbench/src/` | 界面、应用接口、通信实现和领域模型 |
| `tools/FocusIR.Workbench/src/FocusIR.Workbench.Updater/` | 独立安装、启动健康检查与自动回滚器 |
| `tools/FocusIR.Workbench/tests/` | 不连接设备的升级与更新器自动测试 |
| `tools/FocusIR.Workbench/scripts/Start-Workbench.ps1` | 本机启动脚本 |
| `contracts/v1/` | Socket v1 方法目录、JSON Schema 和消息示例 |
| `eng/` | CI、正式发布、升级清单签名及静态校验脚本 |
| `.github/workflows/` | 标签触发的自托管发布工作流 |
| `CODE_MODULES.md` | 模块、关键代码与调用关系 |

配套设备源码位于设备编译主机的 `zs_work/module/focusIR/`，其 `README.md` 与 `CODE_MODULES.md` 说明设备实现。上位机与设备端的 RPC 方法和字段应配套更新。

## 构建

工位使用 Windows 与 .NET 10 SDK。`global.json` 指定 10.0.100 并允许更新的 10.0 feature band；工作台不依赖第三方 NuGet 包。

```powershell
dotnet restore .\FocusIR.Workbench.sln --locked-mode
dotnet build .\FocusIR.Workbench.sln -c Release --no-restore
```

仅编译主程序和更新器、不生成测试程序的命令见 [构建与发布脚本](eng/README.md)。本地修改按项目约定只做源码检查与编译；`eng/ci.ps1` 包含测试执行，保留供既有发布流程使用。

启动工作台可使用 `tools/FocusIR.Workbench/scripts/Start-Workbench.ps1`。默认设备 Socket 端口为 `23233`；连接界面填写设备地址与端口。生产使用前应核对设备端库与上位机的协议版本。

## 主要流程

1. 建立 TCP 连接，发送公开的 `FOCUSIR_FACTORY_WORKBENCH_V1` 工作台标识；读取设备能力、快照并订阅事件。
2. 选择或创建镜头型号，再选择物距配置。已有模型可直接查看双向曲线；新建或显式重新标定时填写配方。
3. 获取相应控制租约后发起初始化、单次 AF、工厂标定、配置激活或复验任务。任务进度由事件与状态查询更新。
4. 标定采样与节点按游标分页读取；可查看曲线、编辑节点草稿，并通过 `profile.update_nodes` 保存应用。保存会使旧验证结果失效。
5. 设备端以本地控制周期执行变倍追焦；工作台提交目标、配置、停止或验证请求，不承担实时电机闭环。

当前单物距标定可使用连续扫焦、局部精调和双向模型。界面按设备能力启用操作；实际支持的方法以 `device.get_capabilities` 为准。详细字段见 `contracts/v1/`。

支持镜头型号和物距配置删除确认，以及 1～9 档长按连续变倍、连续聚焦。长按操作需要 operation 控制租约；连续变倍的 FOCUS 随动由设备状态和有效曲线决定。配套接口、停止行为与人工验收说明见 [配置删除与长按连续运动](docs/config-delete-manual-jog.md)。

## 安全与数据

通信使用工厂网络内的普通 TCP。工作台标识只用于工厂会话识别；运动仍受控制租约、任务互斥、心跳和断线停止约束。软件停止请求的返回状态应与电机实际停稳状态区分。

设备工厂配置根目录为 `/mnt/ext_data/config/backup/dcfactory/`，按镜头型号管理物距模型。上位机只读展示设备提供的固件和清晰度指标版本。构建成功只说明源码可编译；镜头初始化、行程、光学效果和动态联动由人工在真实设备上验收。

## 升级与自动发布

源码仅保存在 GitHub（可以私有），公开二进制发布仓为 `warren-cao/focusir-workbench-releases`。唯一发布工作流为 **FocusIR Release (Self-hosted)**，推送稳定版标签后在 `DESKTOP-AI3E9IB` 上自动运行，先发布 Gitee，再将相同签名附件发布到 GitHub，最后更新静态升级入口；不向 Gitee 推送应用源码。工作流和配置见 [GitHub Actions 发布指南](eng/GITHUB_ACTIONS.md)。

客户端匿名读取新仓 `master` 分支下 `latest/update-manifest.json` 和 `latest/update-manifest.sig` 的公开原始文件，不再查询 Release API。清单验签通过后比较版本，升级包仍从清单指定的正式 Release 附件下载。Gitee 与 GitHub 发布完成后，工作流在一次 Git 提交中更新静态入口。

`v0.3.1` 及更早版本仍使用 API；受限流影响的已安装工位需先从 Release 页面下载并人工安装一次 `v0.3.2` 或更新版本，之后使用固定文件入口检查更新。

发布版本完全由 `v主版本.次版本.修订版本` 标签决定，构建时统一注入应用、程序集和更新器版本。`Directory.Build.props` 仅保留本地开发与依赖还原的默认版本，发版无需修改。锁文件只在项目依赖变化时更新，`CHANGELOG.md` 保留历史说明，不再作为发版必填项。

发布说明自动记录上一个较低稳定标签到当前标签之间的 Git 提交，并附加当前附注标签的说明；内容写入签名清单供上位机展示、安装包中的 `release-notes.md`，以及公开发布仓的 `notes/vX.Y.Z.md` 历史文件。

应用只在用户确认且设备已断开、没有活动任务或控制租约、没有未完成编辑/保存时启动安装。安装由独立更新器完成，使用 `%LocalAppData%\ThermEye\FocusIRWorkbench` 下的版本目录切换并保留上一版本；新版本未在规定时间内报告健康状态时自动回滚。升级过程不会主动停止电机或连接设备。

首次安装使用新发布仓实际发布的稳定版。下载该版 ZIP 与 `SHA256SUMS.txt`，校验摘要后保留原 ZIP，将 ZIP 解压到临时目录，再从该目录执行：

```powershell
.\launcher\FocusIR.Workbench.Updater.exe --install-package <ZIP绝对路径> --version <X.Y.Z> --sha256 <SHA256SUMS中的包摘要>
```

基线安装成功后，开始菜单入口固定指向用户目录中的启动器，后续版本由应用内“检查更新”完成。签名或哈希校验失败时不得绕过检查继续安装。

未经过安装器部署的开发目录运行版本，应先完成上述安装。

后续发版只需将代码提交并推送到 `master`，创建并推送新的 `vX.Y.Z` 标签，即自动发布；无需逐版修改版本文件、更新日志或锁文件。工作流仍保留从 `master` 手动执行的入口。完整步骤见 [`eng/GITHUB_ACTIONS.md`](eng/GITHUB_ACTIONS.md)。

已发布标签和附件不覆盖。上传中断时保留原始签名制品，按发布指南使用当前上传脚本复核与恢复，不重新打包已发布版本。

本工程的自动化测试仅覆盖不接触硬件的纯软件逻辑；流水线不会启动 WPF 工作台、连接镜头或驱动电机。实际镜头、光学效果和工厂标定仍由人工在真实设备上验收。
