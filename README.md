# ETS Tool by @azursmelt（beta 1.0.1）

WinUI 3 版 ETS 助手，包含试卷监听、自动答题、试卷管理、答案提取、抓包调试和中英文界面。

主程序：`_winui\ETS-Assistant-WinUI.exe`  
后台引擎：`ETS-Assistant-Engine.exe` + `_internal_engine\`  
数据与日志：`%APPDATA%\ETS`

## 在新电脑上运行

把整个 `ETS` 文件夹复制到新电脑即可，不需要安装 Python、.NET 或 Windows App Runtime：

- `_winui` 已包含 .NET 8 和 Windows App SDK 自包含运行时。
- `ETS-Assistant-Engine.exe` 和 `_internal_engine` 已包含 Python 运行时及依赖。
- Python 源码只用于重新构建，运行程序时不需要 Python。

仍然需要：

1. Windows 10 1809+ 或 Windows 11，x64。
2. 已安装 ETS（默认 `C:\Program Files (x86)\ETS`，也可以启动后在首页手动选择目录）。
3. 当前 Windows 用户对 `%APPDATA%\ETS` 有读写权限。
4. ETS 支持 `--remote-debugging-port=10086`，自动答题依赖这个本机调试端口。
5. `_winui` 已随附 VC++ 2015–2022 运行库；如果目标系统有额外的策略限制，仍可能需要安装官方 VC++ 运行库。

直接双击：

`_winui\ETS-Assistant-WinUI.exe`

首次使用点击“初始化并打开 ETS”。程序会创建桌面快捷方式和当前用户的登录启动项，并启动后台监听器。以后打开 ETS 时会自动启动监听、转换和答题进程。

## 界面与设置

- **运行概览**：ETS 状态、当前试卷、答题进度和答案；在 ETS 中手动切到第 1 题后，可点“手动重置”重新开始答题。
- **试卷管理**：试卷列表、打开目录、提取答案、分析并复制答案、删除。
- **答题设置**：正确率、随机种子、跳题间隔、开机启动。
- **运行记录**：最近的监听、转换和答题日志。
- **测试工具**：10086 调试端口、JSON 查看、日志导出 ZIP、抓包模式。
- **偏好设置**：应用外观（跟随系统 / 浅色 / 深色）和语言（中文 / English）。
- **关于**：版本、发布时间、项目和作者信息。

配置和界面偏好保存在 `%APPDATA%\ETS`。桌面快捷方式和登录启动项只影响当前 Windows 用户，不修改 ETS 安装目录中的厂商文件。

## 重新构建（开发用）

运行不需要 Python；重新打包才需要：

- Python 3.10+（当前使用 3.14）
- `pip install -r requirements-build.txt`（PyInstaller、Pillow、reportlab）
- .NET 8 SDK
- 运行 `build_ets_assistant_winui3.ps1`

仓库不再随附 `.build-tools` 构建缓存；构建脚本会使用系统 Python/.NET，缺失时按上面的依赖安装。

## 数据与排错

试卷包和日志位于 `%APPDATA%\ETS`。主要日志：

- `ets_answer_listener.log`
- `ets_auto_answer.log`
- `ets_converter.log`
- `ets_winui_startup.log`
- `ets_packet_capture.log`（启用抓包模式后生成）

ETS 已打开但助手显示未连接时，先确认 `127.0.0.1:10086` 可访问；不可访问时关闭 ETS，通过助手重新打开。程序不会点击最终提交按钮，是否删除已提交试卷包由用户确认。
