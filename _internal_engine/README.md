# ETS 自动监听助手

ETS 自动监听助手把试卷监听、Markdown/PDF 转换和 ETS 选择题辅助脚本放进一个 Windows 程序。程序使用 WinUI 3 原生界面，主程序位于 **_winui\ETS-Assistant-WinUI.exe**。后台引擎使用 **ETS-Assistant-Engine.exe** 和同目录的 **_internal_engine** 文件夹。请保留完整的 Codex Sandbox\ETS 文件夹；日常使用不需要手动运行 Python、PowerShell 初始化脚本或编辑 YAML。


## 首次使用

1. 确认 ETS 已安装。
2. 将完整的数字编号试卷包文件夹放进 `%APPDATA%\ETS`。保留试卷包原有的 JSON 文件和目录结构。
3. 打开 **_winui\ETS-Assistant-WinUI.exe**，并保留 **_winui**、**ETS-Assistant-Engine.exe** 和 **_internal_engine**。程序使用 Windows App Runtime 1.8；如果换到没有该运行时的电脑，请先安装[官方运行时](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)。
4. 程序会自动查找 ETS。首次初始化会创建桌面快捷方式和当前 Windows 用户的登录启动项，启动后台监听器，并打开 ETS。
5. 后续可从桌面快捷方式或助手界面的“打开 ETS”启动。后台监听器检测到 ETS 启动时，会自动打开助手窗口并启动监听、转换和答题进程；ETS 关闭后监听和转换进程会结束，助手继续等待 ETS 下次打开。

桌面的“ETS 自动监听助手”和“ETS 自动监听助手（快速启动）”均打开新版 WinUI 界面，“ETS”快捷方式打开 E听说。登录时只启动后台引擎。重新生成或运行安装脚本时，会停止旧版助手并移除 `ETS-Assistant-Fast.exe`、`ETS-Assistant.exe` 和 `_internal_fast`，立即由新引擎接管；新版不再包含旧界面回退入口。

如果找不到 ETS，程序会显示错误并停止初始化。它会检查 Windows 安装记录和常见安装目录。若 ETS 安装在未登记的自定义目录，请先使用常见安装方式重新安装。

## 界面设置

- **目标正确率**：0% 到 100%。每题按该概率选试卷包中的正确答案；其余情况从可见错误选项中随机选择。
- **随机种子**：固定随机结果，方便复现；留空时按 `default` 处理。
- **跳题间隔**：设置“下一步/跳过”按钮重试的最短间隔，范围 0 到 60 秒。
- **登录 Windows 时在后台启动监听器**：开启后，Windows 登录时会等待 ETS 启动。

点“保存设置”后，配置会写入助手程序所在目录的 `ets_answer_config.yaml`。随附版本默认位于 `Codex Sandbox\ETS`。答题 worker 会在启动时读取配置；若 ETS 已经打开，请先关闭再打开 ETS 以应用新设置。

正确率是每题的概率，不保证每 10 题恰好答错固定题数。`1.0` 表示始终选择答案包里的正确答案，`0.0` 表示在至少有一个可见错误选项时选择错误选项。

## 试卷包处理

将完整试卷包目录直接放到 `%APPDATA%\ETS` 下，目录名称以数字开头。ETS 打开时，监听器会启动转换监视；监视开始后新出现的试卷包会被转换为 Markdown，并在系统支持 PDF 组件时同时生成 PDF。文件保存在试卷包的 `converted` 子目录。

自动答题通过本机 `127.0.0.1:10086` 调试接口连接 ETS。助手会创建独立的桌面快捷方式，并尝试添加 `--remote-debugging-port=10086` 参数。是否接受该参数由安装的 ETS 版本决定。

脚本不会替你点击 ETS 的最终提交按钮。ETS 显示提交结果后，若检测到可识别的结果页面，程序会询问是否删除对应试卷包目录；选择删除会永久移除该目录及转换文件。

## 运行记录与排错

界面会显示最近的运行记录。日志文件位于 `%APPDATA%\ETS`：

- `ets_answer_listener.log`：ETS 检测、监听器和进程状态。
- `ets_auto_answer.log`：答案匹配和页面处理状态。
- `ets_converter.log`：试卷包转换状态。

若 ETS 已打开但自动答题没有连接，请用浏览器打开 `http://127.0.0.1:10086/json/version`。页面无法打开时，先关闭 ETS，再通过助手或桌面快捷方式重新启动。若仍无法访问，当前 ETS 版本可能没有转发调试端口参数。

如果桌面快捷方式或登录启动项需要重新创建，可在助手中点击“初始化并启动”。这些设置只影响当前 Windows 用户，不会修改 ETS 安装目录里的厂商文件。

## 功能范围

- 自动操作针对 ETS 页面中的选择题。
- 答案来自本机试卷包，试卷 ID 需要与 ETS 当前内容匹配。
- 自动答题依赖 ETS 页面提供本机调试接口。
- 程序、配置和使用说明保存在 `Codex Sandbox\ETS`；试卷包和运行日志仍保存在 `%APPDATA%\ETS`。程序目录中包含 WinUI 3 和 Python 后台运行组件，日常使用不需要另装 Python 或 YAML 库。界面支持 Windows 10 1809 及以上版本和 Windows 11；WinUI 3 窗口和控件由 Windows App SDK 提供。
