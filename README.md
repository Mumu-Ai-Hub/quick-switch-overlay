# 屏幕快捷切换栏（Quick Apps Overlay 二次开发版）

Windows 10/11 悬浮窗口切换栏：置顶显示在一堆窗口之上（包括全屏远程桌面），点图标直接切到目标窗口，并把任务栏的闪烁提醒映射到图标上。

本项目基于 [vhvb1989/quick-apps-overlay](https://github.com/vhvb1989/quick-apps-overlay)（MIT）二次开发，保留了上游的悬浮切换思路，重写了固定应用、通知提醒、停靠布局和自适应长度等部分。

## 功能

- 自动映射：每秒扫描当前已打开的普通窗口，点击图标切换
- 固定应用：右键运行中的窗口图标可固定；固定项在没有实例时直接启动应用
- 通知提醒：监听任务栏闪烁请求，在对应图标上显示红点；激活或点击后清除。不读取通知内容和未读数
- 停靠与折叠：支持底部/左侧/右侧停靠和横竖切换，拖动吸附，可锁定位置；菜单柄可折叠为一颗小胶囊
- 自适应长度：图标少时收缩，超出屏幕可用空间时图标区可滚动
- Home（⌂）：最小化其他窗口，回到本机桌面
- 快捷键：`Ctrl+Alt+空格` 显示并置顶快捷栏
- 单实例：重复启动只会把已有快捷栏叫到前台

## 环境要求

- Windows 10/11（x64）
- .NET 8 Desktop Runtime（`SelfContained=false`）
- 构建需要 .NET 8 SDK

## 构建与运行

```powershell
dotnet build .\src\QuickAppsOverlay\QuickAppsOverlay.csproj -c Release
dotnet run --project .\src\QuickAppsOverlay\QuickAppsOverlay.csproj -c Release
```

发布单文件版本：

```powershell
dotnet publish .\src\QuickAppsOverlay\QuickAppsOverlay.csproj -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
```

## 图标说明

仓库不附带二进制图标。没有 `Assets\app.ico` 时也能正常构建和运行（托盘使用系统默认应用图标）；想要自己的图标有两种做法：

```powershell
# 方式一：脚本生成一枚占位图标
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\make-icon.ps1

# 方式二：把自己的 .ico 放到 src\QuickAppsOverlay\Assets\app.ico
```

图标引用在 `QuickAppsOverlay.csproj` 里是条件包含（`Exists('Assets\app.ico')`），有文件就自动用，没有也不报错。

## 自动检查

```powershell
pwsh -NoProfile -File .\tests\Run-Checks.ps1
```

`tests/P049Checks` 是一个独立的控制台检查工程，覆盖应用身份匹配、停靠几何、设置读写等纯逻辑分支。

## 已知限制

- 通知提醒依赖 Windows Shell Hook。微软文档明确 Shell Hook 不面向通用应用，未来版本可能改变或移除该行为
- 自动检查与实机验证在 Windows 10 Pro for Workstations 19045 上完成；2026-10-22 复跑 `tests/P049Checks` 为 56 项通过、0 项失败，Release 构建 0 警告 0 错误。Windows 11 和真实全屏 RDP 场景仍待实机验收
- 提醒状态显示 `?` 表示已注册但尚未收到真实闪烁事件，`!` 表示 Shell Hook 注册失败，不等同于「已验证可用」
- 设置文件位于 `%APPDATA%\QuickAppsOverlay\settings.json`，仅本机生效

## 许可

MIT，见 [LICENSE](LICENSE)。上游版权归 vhvb1989 所有。
