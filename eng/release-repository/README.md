# FocusIR Workbench 版本发布

此仓库仅用于发布 FocusIR 自动聚焦工厂工作台的 Windows x64 稳定版本，**不存放应用源码**。

`notes/vX.Y.Z.md` 记录每个发布标签的改动项，和对应签名清单中的发布说明一致。说明由标签之间的 Git 提交与附注标签自动生成，上位机检查更新时会展示清单中的内容。

- [正式版本与下载](https://gitee.com/warren-cao/focusir-workbench-releases/releases)
- 适用平台：Windows x64；程序包自包含 .NET 运行时。
- 已安装的新版本可通过工作台的“帮助 → 检查更新”获取升级。

`v0.3.2` 起，检查更新读取本仓库 `master` 分支中 `latest/update-manifest.json` 与 `latest/update-manifest.sig` 的公开原始文件，不调用 Release 查询 API。升级包仍来自正式 Release 的附件，清单与签名由发布工作流在正式发布完成后同步。

若已安装的 `v0.3.1` 或更早版本因 API 限流无法检查更新，请下载新版本并按下方步骤人工安装一次。

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

安装升级前须断开设备，并结束活动任务、控制租约及未完成的编辑/保存。升级不会自动连接设备或驱动镜头。

版本是否可用以 Release 页面中的实际正式发布为准；预发布版本不用于自动升级。
