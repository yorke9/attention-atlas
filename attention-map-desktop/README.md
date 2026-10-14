# Attention Atlas Desktop / 注意力地图 Windows 全局版

v0.2 将原先的浏览器局部鼠标实验升级为 Windows 全局坐标记录器。主程序可双击运行，不依赖网页、ChatGPT 认证或 Node.js。

## 交付物

- `../注意力地图-Windows版-时长比例版.exe`：可直接运行的桌面程序。
- `../注意力地图-Windows版使用指南.md`：面向使用者的操作说明。
- `AttentionAtlas.cs`：应用、后台采样器、热力渲染器、文件保存及自检。
- `app.manifest`：普通用户权限及 PerMonitorV2 DPI 坐标声明。
- `build.ps1`、`test.ps1`：本机编译与验证脚本。

## 编译和测试

在 PowerShell 中运行 `./build.ps1`，使用系统自带 .NET Framework C# 编译器生成 EXE。引用均来自 .NET Framework，无 NuGet 依赖。

运行 `./test.ps1` 可进行短时真实采样自检，结果位于 `.local/verification/self-test.json`。测试使用不可见的最小化窗口，不移动鼠标、不输入按键、不控制其他软件。

覆盖负坐标多屏映射、距离与停留统计、热力积累、JSON 往返、CSV 与 PNG 导出、最小化后台采样、手动停止冻结、无焦点下倒计时停止、界面绘制。真实采样测试使用缩短的时长，不声称已做五分钟连续运行或所有多屏硬件组合测试。

## 实现

`CursorCapture` 使用独立的 `System.Threading.Timer`，约每 50 ms 调用 Windows `GetCursorPos`，从当前交互桌面获取全局屏幕坐标。它不依赖主窗体焦点、可见性或网页状态。UI 刷新与采样分开，隐藏窗口后继续采样，主界面停止绘制。

`Stopwatch` 控制记录时限。手动停止和到时停止均先在锁内冻结采样，再向界面发送完成事件并保存。不同会话使用唯一 ID；记录列表的快照在同一锁内复制。没有浏览器的 visibilitychange 自动暂停逻辑。

坐标始终保存为原始屏幕像素，并通过显示器虚拟桌面范围映射到热力预览。Manifest 声明 DPI 感知，避免默认 DPI 虚拟化引入坐标偏差。热力场低分辨率累积，绘制时平滑放大。

数据保存在 EXE 旁的 `注意力地图记录` 文件夹：JSON 可回放，PNG 可查看，CSV 可进一步分析。运行中每 10 秒写恢复文件，停止时写最终文件。没有网络、键盘采集、屏幕截图或窗口标题采集逻辑；不更改开机启动或系统权限。

## 已知限制

- Windows 的锁屏、安全桌面和休眠期间可能无法读取当前桌面的光标；不访问其他桌面，不绕过系统访问控制。
- 50 ms 轮询无法保留两次采样间的所有高速细微曲线，路径为采样点之间的近似。
- 当前时限为 1～60 分钟，没有无限记录或日汇总。
- 多屏负坐标已做算法测试；尚未覆盖所有混合 DPI 与热插拔硬件组合。
- 开始时记录显示器轮廓；布局变化后坐标范围可扩展，轮廓需要新会话刷新。
- 这是后台记录器，没有悬浮在其他软件上方的实时热力覆盖层。

## Windows API 依据

- [Microsoft：GetCursorPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getcursorpos)：屏幕坐标读取及当前交互桌面限制。
- [Microsoft：应用默认 DPI 感知声明](https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process)：通过 manifest 指定 DPI 感知模式。

