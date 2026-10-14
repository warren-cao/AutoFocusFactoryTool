# 上位机模块与关键代码

## 调用关系

`MainWindow` 和 `MainViewModel` 接收操作 → `IWorkbenchConnectionService` 约束应用层调用 → `WorkbenchConnectionService` 管理会话、租约与任务 → `ProtocolClient` 编解码并发送 Socket v1 消息 → 设备端 `dc_af_rpc_dispatcher.cpp`。设备事件反向进入 `DeviceStateStore`，再更新界面。

## 工程模块

| 模块 | 主要文件 | 职责 |
| --- | --- | --- |
| WPF 界面 | `App.xaml*`、`MainWindow.xaml*` | 启动、窗口布局、控件事件与停止入口 |
| 页面状态 | `ViewModels/MainViewModel*.cs` | 连接、能力、控制租约、任务状态、操作命令与升级交互 |
| 镜头与物距 | `MainViewModel.Profiles.cs`、`MainViewModel.LensInitialization.cs` | 镜头型号、物距配置、初始化、激活与复验 |
| 曲线编辑 | `MainViewModel.CurveEditing.cs`、`CalibrationChartsViewModel*.cs`、`Controls/CalibrationPlot.cs` | 节点拖动草稿、采样/模型可视化、保存应用和 CSV 导出 |
| 标定参数 | `MainViewModel.AutofocusConfiguration.cs`、`Domain/FactoryCalibrationData.cs` | AF 参数、工厂配方、节点/样本读取与解释 |
| 应用边界 | `Application/IWorkbenchConnectionService.cs`、`IUpdateService.cs` | 界面依赖的连接、业务操作与升级契约 |
| 会话实现 | `Infrastructure/WorkbenchConnectionService.cs` | 握手、能力校验、RPC、心跳、租约续期、任务查询、重连、事件缺口恢复 |
| 升级服务 | `Infrastructure/GiteeUpdateService.cs`、`Domain/UpdateModels.cs` | Gitee Release 查询、清单验签、版本判断、下载及完整性校验 |
| 独立更新器 | `FocusIR.Workbench.Updater/*` | 用户目录安装、版本切换、启动健康检查、回滚、自更新和快捷方式 |
| 状态缓存 | `Infrastructure/DeviceStateStore.cs` | 快照与事件合并，过滤失序或过期状态 |
| 传输协议 | `Protocol/ProtocolClient.cs`、`FrameCodec.cs`、`PrioritySendQueue.cs`、`ProtocolLimits.cs` | TCP 长度帧、JSON 请求响应关联、发送优先级与协议限额 |
| 领域类型 | `Domain/DeviceModels.cs` | 连接状态、设备摘要、数据来源和租约等类型 |
| 共享合同 | `contracts/v1/method-catalog.json`、`schemas/`、`samples/` | RPC 方法、参数约束与示例消息 |
| 构建发布 | `Directory.Build.props`、`eng/*.ps1`、`.gitee/*.yml` | 集中版本、锁定构建、纯软件测试、签名打包和 Gitee Release |
| 自动测试 | `tools/FocusIR.Workbench/tests/` | 升级协议、下载校验、包安装、路径安全、状态切换和回滚的纯软件验证 |

`src` 下工作台项目的依赖方向是 App → Application/Infrastructure/Domain，Infrastructure → Application/Protocol/Domain；Protocol 与 Domain 不依赖 WPF。独立 Updater 不引用 WPF 主程序，以便在主程序退出后完成版本切换和回滚。解决方案入口为 `FocusIR.Workbench.sln`。

## 关键行为与维护入口

- 连接：`WorkbenchConnectionService.OpenSessionAsync` 校验握手、读取能力与快照，再开始心跳和事件订阅；`ReconnectLoopAsync` 恢复只读状态，不复用已失效的控制租约。
- RPC：`ProtocolClient` 用请求 ID 配对响应。运动或任务请求超时后，连接服务查询原请求状态，避免重发导致重复物理动作。
- 事件：`DeviceStateStore` 以快照为基线合并可靠事件；发现事件缺口时重新读取快照并订阅。
- 任务：`MainViewModel` 负责用户操作与状态展示；设备持续任务仍由设备端执行，工作台只查询、取消或停止。
- 标定：`FactoryCalibrationData` 读取分页记录；`CalibrationChartsViewModel` 展示双向节点和原始清晰度；曲线编辑保存前使用当前模型 ID 做并发校验。
- 协议变更：先核对 `contracts/v1/method-catalog.json` 与相关 Schema，再同步修改上位机连接服务和设备端分发器。能力表决定界面是否开放相应操作。
- 发布：`eng/ci.ps1` 是本机与流水线共用的质量入口；`eng/release.ps1` 只接受与集中版本和更新日志一致、且属于 `master` 历史的稳定版标签，生成四个不可覆盖的 Release 附件。
- 签名：`eng/New-UpdateSigningKey.ps1` 生成 ECDSA P-256 密钥；私钥只进入 Gitee 加密变量，客户端仅按 `keyId` 内置并信任对应公钥。
- 升级：应用仅负责匿名检查、验签和下载；设备断开且无任务/租约/编辑后才启动独立更新器。更新器在新版本窗口报告健康后提交升级，否则自动切回上一版。

本说明对应当前解决方案中的生产项目及其构建发布基础设施。
