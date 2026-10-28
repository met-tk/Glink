# Windows 安装包

`Build-Installer.ps1` 生成 `artifacts/Glink-1.2.0-Setup-x64.msi` 和 SHA-256 校验文件。构建只使用本机 .NET SDK、已安装的 Visual Studio C++ 可再发行运行库、Windows 自带的 `makecab.exe` 和 Windows Installer COM API，不下载或执行安装工具。

```powershell
.\packaging\Build-Installer.ps1
```

依赖需事先还原；发布使用 `--no-restore`。产物包含 .NET 和 Windows App SDK 运行时，不裁剪、不附带测试入口或用户数据库。可用 `-VcRuntimeDirectory` 指定本机官方 `x64\Microsoft.VC143.CRT` 目录；复制前校验 DLL 签名。

MSI 按当前用户安装，默认位置为 `%LOCALAPPDATA%\Programs\Glink`；安装页可编辑路径或浏览选择当前账户可写的文件夹。修复和升级沿用保存的安装位置。安装器创建开始菜单入口，支持标准 Windows 应用卸载、修复和主版本升级。包内没有脚本、可执行自定义动作或下载引导程序；仅使用 Windows Installer 内置的 Type 51 属性赋值动作与标准文件操作。升级和卸载前请关闭 Glink；安装器不会强制关闭应用或自动重启系统。

卸载默认保留资料。用户在卸载确认页勾选“同时删除灵感库、媒体和设置”后，才会移除固定 `%LOCALAPPDATA%\Glink` 中应用管理的数据文件和 `media`、`.undo` 两个目录内的文件；不递归未知子目录，不清理旧版 `%LOCALAPPDATA%\Moodia` 或 `GLINK_DATA_ROOT` 指向的其他位置。修复和升级始终保留资料。静默卸载仅在明确传入 `REMOVEUSERDATA=1` 时启用同样的限定清理。

独立验收使用另一组产品与升级 GUID、项目内自选程序目录和固定 `packaging/.build/smoke/user-data` 测试资料库，不会覆盖正式产品：

```powershell
.\packaging\Build-Installer.ps1 -SkipPublish -SmokeTest
.\packaging\Test-Installer.ps1
```

验收包括自选安装位置、逐文件哈希、独立资料库启动、修复缺失文件、模拟升级、拒绝降级、默认保留资料、明确选择删除资料，以及干扰路径参数无法扩大删除范围；日志在 `packaging/.build/smoke/`。模拟升级仅改变测试安装器版本，不改变正式应用版本。

测试资料放在项目目录，以避免 MSIX 打包的开发工具将 AppData 写入重定向到自身缓存。测试脚本在运行安装器之前核对标记文件的物理路径，确保测试进程与 Windows Installer 操作的是同一目录；正式包仍固定清理 `%LOCALAPPDATA%\Glink`。

目前没有制作人的可信代码签名证书，MSI 未签名；SHA-256 校验用于检查文件是否一致，不等于发布者认证。发布时可由制作人使用自己的有效证书与微软 SignTool 签名，然后重新生成校验值。不要关闭防护、添加排除项或安装自签根证书。
