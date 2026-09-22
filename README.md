<div align="center">

<img src="launcher-src/Assets/home-banner.jpg" width="760" alt="Forge Neo 启动器 · 首页">

# Forge Neo 启动器

**给 [sd-webui-forge-neo](https://github.com/Haoming02/sd-webui-forge-classic/tree/neo) 的 Windows 图形化启动器**

一键启动 · 实时日志 · 环境体检 · 便携部署 · 主题切换

![License](https://img.shields.io/badge/license-AGPL--3.0-blue)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-0078D4)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)

</div>

---

一个单文件、免安装的 WPF 启动器。把 exe 丢进 Forge Neo 根目录双击就行 —— 不用记命令行参数，不用开黑窗口；环境缺什么它会告诉你，缺虚拟环境能一键建好。

**系统要求**：Windows 10 / 11（x64）。**不需要预装 .NET** —— 运行时已经打进 exe 了。

## 功能

| 模块 | 说明 |
|---|---|
| **首页** | 大字启动按钮、常用文件夹快捷入口、运行状态一览 |
| **一键部署** | 打开即体检：内核文件 → venv → 随包运行时。不合格自动跳转 |
| **实时日志** | 直接读 Forge 的 stdout，不伪造百分比；跑图时进度按真实 tqdm 走 |
| **内核更新** | 检测上游仓库的分支与提交，只更新 Forge Neo 本体 |
| **插件管理** | 独立一页：列出全部扩展（含手动解压、没有 `.git` 的那些），装 / 卸 / 启停 / 逐项更新 |
| **模型管理** | 独立一页：列出每个模型目录里**有什么文件、多大**（含你自己建的目录与高级选项里追加的目录），可搜索、可一键打开 |
| **PyTorch 环境** | 按显卡算力挑选 torch / torchvision 组合，独立安装进 venv |
| **高级选项** | 常用命令行参数可视化（含服务端口，被占用时可自动让位），含参数预览 |
| **依赖下载源** | 国内镜像（阿里云 PyPI + 上海交大 torch wheel）/ 官方源，一键切换 |
| **主题** | 浅色 / 深色，偏好写进 `launcher.cfg` |

## 快速开始

1. 到 [Releases](../../releases) 下载 `ForgeNeoLauncher.exe`；
2. 把它放进 **Forge Neo 的根目录**（与 `launch.py`、`webui.py` 同级）；
3. 双击运行。

启动器用 exe 自身的位置推断 Forge 根目录，不需要配置路径。部署完成之后**不要再移动目录** —— `venv` 里的 `pyvenv.cfg` 记的是解释器的绝对路径。

### 首次启动会发生什么

打开时先做一次体检，结果是三态之一：

| 状态 | 条件 | 界面表现 |
|---|---|---|
| `Ready` | 内核齐备 + venv 可用 | 直接进日志页，无角标 |
| `NeedsVenv` | 内核齐备，但没有 venv | 自动切到【一键部署】，按钮亮起 |
| `Broken` | 内核文件缺失 | 切到部署页但按钮禁用，提示重新解压 |

`NeedsVenv` 时点【开始部署】即可。它只做一件事：用随包的 Python 与 uv 建好 venv（约 1 秒）。**之后 Forge 自己会去装 torch 等依赖**，首次约 4–5 GB，耗时取决于网速。

> 为什么依赖不代劳：哪个 torch 配哪个 torchvision、xformers / sageattention / flash-attn 各配什么版本，上游 `prepare_environment()` 里写得清清楚楚，而且**随版本变化**。启动器另起一套等于把它抄第二遍，上游一升级就失配。

## 自己构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

产物为 `_published\ForgeNeoLauncher.exe` —— 自包含单文件，约 73 MB。

## 目录结构

```
launcher-src/
  App.xaml(.cs)          程序入口
  MainWindow.xaml(.cs)   主界面（侧栏导航 + 各视图）
  AppPaths.cs            所有路径的唯一来源（随 exe 位置自适应）
  AppInfo.cs             版本号与代号
  LauncherConfig.cs      launcher.cfg 读写
  AdvancedOptions.cs     高级选项模型 → 启动参数
  DownloadSource.cs      下载源（镜像 / 官方）
  DeployState.cs         三态体检
  DeployManager.cs       建 venv（只管这一步）
  TorchManager.cs        PyTorch 环境
  PortGuard.cs           端口探测与归属判断（是谁占着）
  UpdateHttp.cs          HTTPS 拉取（GitHub 要带 UA，否则 403）
  UpdateInfo.cs          两个更新模型 + UpdateState
  CoreUpdater.cs         内核的检测与更新
  ExtensionManager.cs    插件：扫描 / 检测 / 更新 / 装 / 卸 / 启停
  ModelCatalog.cs        模型目录的扫描 / 体积 / 过滤（清单派生自磁盘）
  GitRunner.cs           所有 git 调用的唯一收口
  RecycleBin.cs          删除走系统回收站
  HomeFolders.cs         首页文件夹入口
  Theme.cs  UiFx.cs      主题与动效
build.ps1                构建脚本
```

> 更新检测一旦成功，界面只刷新**受影响的那一项**（读一次本地 HEAD，不联网）—— 会发网络请求的只有你亲手点的【检测内核更新】/【检测插件更新】。

## 设计上的一条主线

界面不直接拼命令行，命令行也不反向解析界面 —— 两边都从同一个模型投影出来，所以**预览里看到什么，实际启动就用什么**。`AppPaths` 同理：所有路径只从一个地方推导，没有第二份真相。

同样的道理也用在【模型管理】上：那一页显示的目录**来自磁盘本身**（扫 `models\` 下真实存在的子目录），
而不是照着一张写死的清单念 —— 所以你**自己建的目录**、以及上游新增的目录都会自动出现，不会"明明放在那儿、列表里却没有"。

## 与 Forge Neo 的关系

本仓库**只包含启动器源码**，不含 sd-webui-forge-neo 的任何代码。启动器以「启动子进程 + 传递命令行参数」的方式驱动 Forge，属于独立程序。

Forge Neo 本体请到上游仓库获取。

## 许可证

[AGPL-3.0](LICENSE)。© 2026 NavarraCN
