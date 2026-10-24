# LocalRDP — 桌面分身

从 [BetterGI（更好的原神）](https://github.com/babalae/better-genshin-impact) 中剥离出来的**独立桌面分身工具**：只保留"桌面分身"这一个功能，并新增了 16:9 分辨率切换。程序集名为 `DeskClone`。

桌面分身基于 Windows 自带的 **RDP Child Session** 能力：在本机创建一个隔离的真实 Windows 登录会话，并通过内嵌的 RDP 控件把它以窗口形式显示在主桌面上。不需要虚拟机、不需要第三方驱动。

## 功能

- **启动/连接桌面分身**：一键创建 Child Session 并建立 RDP 连接（自动环境预检、失败自动重试）
- **16:9 分辨率切换**：1280×720 / 1600×900 / 1920×1080 / 2560×1440 / 3840×2160
  - 已连接时切换会自动断开并重连 RDP，**分身会话不受影响，里面的程序不会退出**
  - 未连接时切换则保存设置，下次连接生效
- **观看模式**：只显示画面，本机键鼠不会进入分身
- **以管理员权限启动程序**：通过临时计划任务把任意 exe 以管理员身份启动到分身里（无 UAC 弹窗）
- **系统托盘**：隐藏窗口保持 RDP 连接，双击托盘唤回；支持"关闭分身并退出"与"保留分身，仅退出程序"两种退出方式
- 其他：窗口置顶、自适应/1:1 显示、保持 16:9 宽高比、系统组合键发送位置切换、分身静音、发送 Win+D / Win+Tab

## 构建与运行

要求 .NET 10 SDK、Windows 10 22H2 及以上。

```bash
dotnet build DeskClone/DeskClone.csproj -c Release
# 发布为单文件独立程序（自带 .NET 运行时，目标机器无需安装任何依赖）
dotnet publish DeskClone/DeskClone.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

生成的 `DeskClone.exe` 需要**管理员权限**运行（清单已内置 `requireAdministrator`）：
启用/注销 Child Session、注册计划任务都依赖管理员权限。

## 使用说明

1. 以管理员身份运行 `DeskClone.exe`，点击"启动并连接"
2. 首次连接时 Windows 会创建分身会话，登录完成后窗口中央即显示分身桌面
3. 在标题栏下拉框中切换分辨率；通过"控制中心"菜单调整窗口与快捷键行为
4. 需要在分身里装软件/跑程序时，用"控制中心 → 以管理员权限启动…"选择 exe

### 已知限制（继承自原实现）

- 与 RDP Wrapper 不兼容（启动前会自动检测提醒）
- "仅允许 Windows Hello 登录"的微软账户会导致分身登录失败（预检会提示）
- 分辨率上限受 RDP 与显卡驱动约束（4K 一般可用）

## 与原版 BetterGI 的差异

| 项目 | 原版 BetterGI | DeskClone |
| --- | --- | --- |
| 桌面分身核心（Child Session + RDP ActiveX） | ✓ | ✓ 完整保留 |
| 分辨率 | 固定 1920×1080 | 标准 16:9 五档可切换 |
| 在分身里启动 BetterGI | ✓（配合自动化任务） | 移除（无自动化功能） |
| 游戏鼠标模式 / 相对鼠标转发 | ✓ | 移除（依赖多实例 IPC） |
| 自动化、调度、脚本等其余全部功能 | ✓ | 全部移除 |

代码提取自 BetterGI `refactor/20260930`（2026-10），许可证遵循原项目（GPL-3.0）。
