# FocusIR Workbench 版本发布

此仓库仅用于发布 FocusIR 自动聚焦工厂工作台的 Windows x64 稳定版本，**不存放应用源码**。

- [正式版本与下载](https://gitee.com/warren-cao/focusir-workbench-releases/releases)
- 适用平台：Windows x64；程序包自包含 .NET 运行时。
- 已安装的新版本可通过工作台的“帮助 → 检查更新”获取升级。

## 下载与首次安装

请选择 Release 页面中的正式版本，每个版本包含以下四个文件：

```text
FocusIR.Workbench-vX.Y.Z-win-x64.zip
update-manifest.json
update-manifest.sig
SHA256SUMS.txt
```

**不要下载 Gitee 自动生成的源码 ZIP/TAR 作为安装包**；源码压缩包只有本仓库的发布说明。

首次安装时先核对 ZIP 的 SHA-256 与 `SHA256SUMS.txt` 一致，再保留原 ZIP、解压到临时目录，执行包中的启动/更新器：

```powershell
.\launcher\FocusIR.Workbench.Updater.exe --install-package <原ZIP绝对路径> --version <X.Y.Z> --sha256 <ZIP的SHA256>
```

请将占位符替换为所下载版本的实际值。安装到当前用户的本地应用数据目录，不需要管理员权限；正常使用开始菜单中的工作台入口。

## 升级与安全

升级清单使用 ECDSA P-256 / SHA-256 签名，客户端会验证清单、包大小和 SHA-256。签名或摘要校验失败时请停止安装，不要绕过检查。

当前验签公钥标识为 `focusir-release-2026`，以下仅为可公开的公钥，**不包含签名私钥**：

```text
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJ2lia/G7FPhmTB5G7e9b44jW9msT
fHfHgVw86hhtFFycR+2qndgLkKr8IxB9YOUJ6WAgLQgJtrfS35xkh784zQ==
-----END PUBLIC KEY-----
```

应用内置受信任公钥；本说明中的公钥不能替代客户端的信任校验。`SHA256SUMS.txt` 用于完整性核对，本身不能替代数字签名。

## 旧升级源迁移

旧版 `v0.2.0` 工作台通过原发布地址取得 `v0.2.1` 桥接更新。安装完成后，程序自动改为查询本仓库。

桥接版本的新、旧发布地址使用**完全相同的程序 ZIP**；因下载地址不同，外部升级清单分别签名。后续版本只在本仓库发布。

旧升级入口会保留到工位迁移完成。迁移不会自动连接设备或驱动镜头；安装升级前须断开设备，并结束活动任务、控制租约及未完成的编辑/保存。

版本是否可用以 Release 页面中的实际正式发布为准；预发布版本不用于自动升级。
