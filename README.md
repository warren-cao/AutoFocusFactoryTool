# FocusIR 工厂自动聚焦工作台

本仓库是 Windows 工厂工作台与设备端 FocusIR 模块配套使用的上位机工程。工作台负责连接设备、读取能力和状态、管理镜头型号与物距配置、发起聚焦和标定任务、展示采样与曲线、提交人工曲线编辑。电机控制、清晰度采样、模型保存和实时变倍追焦在设备端执行。

## 目录与版本

| 路径 | 内容 |
| --- | --- |
| `FocusIR.Workbench.sln` | .NET 10 WPF 工作台解决方案 |
| `tools/FocusIR.Workbench/src/` | 界面、应用接口、通信实现和领域模型 |
| `tools/FocusIR.Workbench/scripts/Start-Workbench.ps1` | 本机启动脚本 |
| `contracts/v1/` | Socket v1 方法目录、JSON Schema 和消息示例 |
| `CODE_MODULES.md` | 模块、关键代码与调用关系 |

配套设备源码位于设备编译主机的 `zs_work/module/focusIR/`，其 `README.md` 与 `CODE_MODULES.md` 说明设备实现。上位机与设备端的 RPC 方法和字段应配套更新。

## 构建

工位使用 Windows 与 .NET 10 SDK。`global.json` 指定 10.0.100 并允许更新的 10.0 feature band；工作台不依赖第三方 NuGet 包。

```powershell
dotnet restore .\FocusIR.Workbench.sln --locked-mode
dotnet build .\FocusIR.Workbench.sln -c Release --no-restore
```

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

本工程约定只做源码检查与编译，不自动运行测试程序或驱动镜头。
