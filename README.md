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
| `.gitee/` | Gitee Go 的 CI 与 Release 流水线模板 |
| `CODE_MODULES.md` | 模块、关键代码与调用关系 |

配套设备源码位于设备编译主机的 `zs_work/module/focusIR/`，其 `README.md` 与 `CODE_MODULES.md` 说明设备实现。上位机与设备端的 RPC 方法和字段应配套更新。

## 构建

工位使用 Windows 与 .NET 10 SDK。`global.json` 指定 10.0.100 并允许更新的 10.0 feature band；工作台不依赖第三方 NuGet 包。

```powershell
dotnet restore .\FocusIR.Workbench.sln --locked-mode
dotnet build .\FocusIR.Workbench.sln -c Release --no-restore
```

也可以在干净工作区运行 `pwsh -NoProfile -File .\eng\ci.ps1`，统一执行锁定还原、Release 警告即错误编译及纯软件自动测试，结果写入 `artifacts/test-results/`。

启动工作台可使用 `tools/FocusIR.Workbench/scripts/Start-Workbench.ps1`。默认设备 Socket 端口为 `23233`；连接界面填写设备地址与端口。生产使用前应核对设备端库与上位机的协议版本。

## 主要流程

1. 建立 TCP 连接，发送公开的 `FOCUSIR_FACTORY_WORKBENCH_V1` 工作台标识；读取设备能力、快照并订阅事件。
2. 选择或创建镜头型号，再选择物距配置。已有模型可直接查看双向曲线；新建或显式重新标定时填写配方。
3. 获取相应控制租约后发起初始化、单次 AF、工厂标定、配置激活或复验任务。任务进度由事件与状态查询更新。
4. 标定采样与节点按游标分页读取；可查看曲线、编辑节点草稿，并通过 `profile.update_nodes` 保存应用。保存会使旧验证结果失效。
5. 设备端以本地控制周期执行变倍追焦；工作台提交目标、配置、停止或验证请求，不承担实时电机闭环。

当前单物距标定可使用连续扫焦、局部精调和双向模型。界面按设备能力启用操作；实际支持的方法以 `device.get_capabilities` 为准。详细字段见 `contracts/v1/`。

## 安全与数据

通信使用工厂网络内的普通 TCP。工作台标识只用于工厂会话识别；运动仍受控制租约、任务互斥、心跳和断线停止约束。软件停止请求的返回状态应与电机实际停稳状态区分。

设备工厂配置根目录为 `/mnt/ext_data/config/backup/dcfactory/`，按镜头型号管理物距模型。上位机只读展示设备提供的固件和清晰度指标版本。构建成功只说明源码可编译；镜头初始化、行程、光学效果和动态联动由人工在真实设备上验收。

## 升级与自动发布

应用版本统一配置在 `Directory.Build.props` 的 `VersionPrefix`。稳定版本使用 `v主版本.次版本.修订版本` 标签，并通过公开 Gitee Release 发布 `win-x64` 自包含 ZIP、升级清单、ECDSA 签名和 SHA-256 校验文件。

应用只在用户确认且设备已断开、没有活动任务或控制租约、没有未完成编辑/保存时启动安装。安装由独立更新器完成，使用 `%LocalAppData%\ThermEye\FocusIRWorkbench` 下的版本目录切换并保留上一版本；新版本未在规定时间内报告健康状态时自动回滚。升级过程不会主动停止电机或连接设备。

现有 `v0.1.0` 不具备自升级能力，必须人工部署一次 `v0.2.0` 基线：下载 Release 的 ZIP 与 `SHA256SUMS.txt`，校验摘要后保留原 ZIP，将 ZIP 解压到临时目录，再从该目录执行：

```powershell
.\launcher\FocusIR.Workbench.Updater.exe --install-package <ZIP绝对路径> --version 0.2.0 --sha256 <SHA256SUMS中的包摘要>
```

基线安装成功后，开始菜单入口固定指向用户目录中的启动器，后续版本由应用内“检查更新”完成。签名或哈希校验失败时不得绕过检查继续安装。

Gitee CI 在 `master` 推送和代码评审时运行；正式发布仅由受保护的 `vX.Y.Z` 标签触发。发布节点必须是专用 Windows x64 主机，并在 Gitee UI 中绑定主机组、保护分支/标签和加密签名变量。完整接入步骤、密钥轮换和本地发布命令见 [`eng/README.md`](eng/README.md)。

第一次执行 CI/CD 时请按 [`eng/CICD_GUIDE.md`](eng/CICD_GUIDE.md) 顺序配置 Windows 主机、加密变量、两条流水线，再推送版本标签。

本工程的自动化测试仅覆盖不接触硬件的纯软件逻辑；流水线不会启动 WPF 工作台、连接镜头或驱动电机。实际镜头、光学效果和工厂标定仍由人工在真实设备上验收。
