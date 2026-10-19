# ETS Tool by @azursmelt（beta 1.0.1）

WinUI 3 版 ETS 助手，包含试卷监听、自动答题、试卷管理、答案提取、抓包调试和中英文界面。

主程序：`_winui\ETS-Assistant-WinUI.exe`  
后台引擎：`ETS-Assistant-Engine.exe` + `_internal_engine\`  
数据与日志：`%APPDATA%\ETS`

## 在新电脑上运行

把整个 `ETS` 文件夹复制到新电脑即可，不需要安装 Python、.NET 或 Windows App Runtime。必须保留 `ETS-Assistant-Engine.exe` 旁边的整个 `_internal_engine` 文件夹；不要只分发 EXE，也不要从文件夹中单独挑选文件。引擎依赖该目录中的 Python DLL、标准库扩展和第三方库。

- `_winui` 已包含 .NET 8 和 Windows App SDK 自包含运行时。
- `ETS-Assistant-Engine.exe` 和 `_internal_engine` 已包含 Python 运行时及依赖。
- Python 源码只用于重新构建，运行程序时不需要 Python。

仍然需要：

1. Windows 10 1809+ 或 Windows 11，x64。
2. 已安装 ETS（默认 `C:\Program Files (x86)\ETS`，也可以启动后在首页手动选择目录）。
3. 当前 Windows 用户对 `%APPDATA%\ETS` 有读写权限。
4. ETS 支持 `--remote-debugging-port=10086`，自动答题依赖这个本机调试端口。
5. `_winui` 已随附 VC++ 2015–2022 运行库；如果目标系统有额外的策略限制，仍可能需要安装官方 VC++ 运行库。

如果启动时报 `No module named '_socket'`，请先检查程序目录中是否存在 `_internal_engine\_socket.pyd` 和 `python3*.dll`，并确认它们与 `ETS-Assistant-Engine.exe` 来自同一次完整构建。文件缺失通常表示复制或解压不完整，或安全软件隔离了运行库文件；重新解压完整程序包，并检查安全软件的隔离记录。不要只复制 `ETS-Assistant-Engine.exe`。

直接双击：

`_winui\ETS-Assistant-WinUI.exe`

首次使用点击“初始化并打开 ETS”。程序会创建桌面快捷方式和当前用户的登录启动项，并启动后台监听器。以后打开 ETS 时会自动启动监听、转换和答题进程。

## 界面与设置

- **运行概览**：ETS 状态、当前试卷、答题进度和答案；在 ETS 中手动切到第 1 题后，可点“手动重置”重新开始答题。
- **试卷管理**：试卷列表、打开目录、提取答案、分析并复制答案、删除。
- **答题设置**：正确率、随机种子、跳题间隔、开机启动、口语模式和 DeepSeek API Key。
- **运行记录**：最近的监听、转换和答题日志。
- **测试工具**：10086 调试端口、JSON 查看、日志导出 ZIP、抓包模式和 DeepSeek 口语测试。
- **偏好设置**：应用外观（跟随系统 / 浅色 / 深色）和语言（中文 / English）。
- **关于**：版本、发布时间、项目和作者信息。

配置和界面偏好保存在 `%APPDATA%\ETS`。桌面快捷方式和登录启动项只影响当前 Windows 用户，不修改 ETS 安装目录中的厂商文件。

## DeepSeek 口语测试

在“答题设置”页填入 DeepSeek API Key 并保存，密钥会用 Windows DPAPI 加密后存放在 `%APPDATA%\ETS\deepseek_api_key.bin`，不会写入配置文件或仓库。

同一页还可以切换口语模式：

- **标答模式**：直接从试卷包的朗读/看图/问答数据里提取推荐答案（与转换后 md 中的 `Model answer`、`Reference text` 同源），不调用 DeepSeek，适合直接送 TTS。
- **灵活模式**：调用 `deepseek-flash`，每次请求附带题干和 `llm_stt.md` 生成回答。

“测试工具”页的“DeepSeek 口语测试”卡片可以输入题干并发送：每次请求都会把题干和 `llm_stt.md` 附件一起发给 `deepseek-flash`，返回的正文显示在下方，可直接复制。附件优先从程序目录、`_winui\Assets` 读取，找不到时回退到 `%USERPROFILE%\Downloads\llm_stt.md`。

点击“读取所有试卷口语题”会像试卷列表一样扫描本机所有试卷包，列出其中有朗读、看图说话或问答类题目的试卷；先在试卷下拉框选一份，再选题。标答模式直接显示推荐答案，灵活模式则把题干送到 DeepSeek。

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

## 运行位置与权限

监听器需要写入 `%APPDATA%\ETS`（日志和答题历史）。如果程序目录位于 Windows 沙箱或受完整性级别限制的位置（例如 `Codex Sandbox` 工作区），Windows 会以低完整性级别启动程序，监听器写不了日志，答题进程也就不会启动。此时请把运行目录放到普通用户目录（例如 `%LOCALAPPDATA%\Programs\ETS-Assistant`），并让桌面快捷方式和登录启动项指向该目录。

