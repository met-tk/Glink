# Glink

个人轻量化多模态灵感沉淀库。收纳文本、音频、图片、视频四类数字资产，具备自由二维画布与紧凑装箱排布。

## 核心特性

- 多模态沉淀：支持文本、图片、音频、视频的无缝收集与就地浏览。
- 空间画布交互：提供具备空间记忆的自由二维画布、自动紧凑装箱排布（MaxRects 算法）、自定义布局快照与平铺整理。
- 无感采集录入：支持全域文件拖拽、剪贴板快速粘贴（Ctrl + V）、富文本与多文件解析入库。
- 情绪意向分类：预设 8 色循环调色板与层级分类管理，支持拖拽排序与分类集合收纳。
- 本地优先与隐私安全：基于 SQLite 本地数据库与本地沙盒存储，数据完全由用户掌控。
- 纯净沉浸设计：遵循现代 Windows 原生视觉语言（WinUI 3 / Windows App SDK），低饱和深色基底，克制优雅。

## 技术架构

- UI 框架：Windows App SDK / WinUI 3 (.NET 10)
- 底层架构：.NET 10 + C# 13
- 本地存储：SQLite (Microsoft.Data.Sqlite) + 本地沙盒媒体存储
- 排布引擎：MaxRects 二维装箱算法 + 径向平铺

## 快速上手

### 环境要求

- 操作系统：Windows 10 version 1809 (Build 17763) 或更高版本（推荐 Windows 11）
- 开发环境：.NET 10 SDK、Visual Studio 2022 / 2026（包含 Windows 应用开发工作负载）

### 编译与运行

```powershell
# 还原依赖并编译 Release 版本
dotnet build Glink/Glink.csproj -c Release -p:Platform=x64

# 或者使用快速启动脚本
.\启动Glink.bat
```

### 安装包制作

项目内置了基于 Windows Installer 的离线 MSI 安装包打包脚本：

```powershell
.\packaging\Build-Installer.ps1
```

构建产物将输出至 `artifacts/` 目录。

## 开源协议

本项目采用 MIT 许可证。详见 [LICENSE](LICENSE)。
