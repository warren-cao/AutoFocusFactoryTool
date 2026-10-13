# FocusIR Socket v1 合同

本目录保存上位机和设备端共享的协议资料。`protocol-version.json` 指定版本，`method-catalog.json` 列出 RPC 方法，`schemas/` 定义消息与业务参数结构，`samples/` 提供请求、响应和事件示例。实现应以当前双方源码及设备 `device.get_capabilities` 公布的方法为准；Schema 与示例用于评审和维护，不会在运行时自动代替设备端参数校验。

## 传输与会话

普通 TCP 使用 4 字节长度前缀和 UTF-8 JSON，最大消息长度由两端协议限额约束。`session.open` 发送 `workbench_tag=FOCUSIR_FACTORY_WORKBENCH_V1`；设备返回相同标识并建立工厂会话。`client_id` 标识请求归属。64 位序号、单调时间与控制 epoch 以十进制 JSON 字符串传输，镜头原始位置为 int32。

运动和工厂任务需要对应控制租约，并受心跳、任务互斥与断线处理约束。请求超时表示结果未知，客户端查询原请求 ID 的状态，不重发原物理操作。可靠事件发生缺口时，客户端重新读取快照并订阅。

## 当前业务范围

合同覆盖设备能力与状态、镜头型号、物距配置、AF、工厂标定、节点与样本分页读取、曲线节点人工更新，以及本地变倍追焦配置和任务。`factory.start` 的配方、镜头型号和物距标识应与设备端存储规则保持一致。动态运动和光学质量需人工在真实设备验收。

协议修改时同步更新方法目录、相关 Schema、上位机 `WorkbenchConnectionService` 与设备端 `dc_af_rpc_dispatcher.cpp`、`dc_af_capability.cpp`。普通 TCP 应只用于隔离的工厂网络。
