# ETS Tool by @azursmelt（正式版 1.1.2）

WinUI 3 桌面助手，提供试卷监听、自动答题、试卷管理、答案与 PDF 解析、口语和 TTS 辅助功能，并支持中英文界面。

主程序：`_winui\ETS-Assistant-WinUI.exe`  
后台引擎：`ETS-Assistant-Engine.exe` + `_internal_engine\`  
数据与日志：`%APPDATA%\ETS`

## 在新电脑上运行

把整个 `ETS` 文件夹复制到新电脑即可，主程序和自动答题不需要安装 Python、.NET 或 Windows App Runtime。必须保留 `ETS-Assistant-Engine.exe` 旁边的整个 `_internal_engine` 文件夹；不要只分发 EXE，也不要从文件夹中单独挑选文件。引擎依赖该目录中的 Python DLL、标准库扩展和第三方库。

- `_winui` 已包含 .NET 8 和 Windows App SDK 自包含运行时。
- `ETS-Assistant-Engine.exe` 和 `_internal_engine` 已包含 Python 运行时及依赖。
- Python 源码只用于重新构建；主程序、自动答题、手动 TTS 播放和“一键自动口语”都使用随程序自包含的运行时，不需要安装 Python。
- `tools\CablePlayer` 已自包含，不需要安装 .NET。

仍然需要：

1. Windows 10 1809+ 或 Windows 11，x64。
2. 已安装 ETS（默认 `C:\Program Files (x86)\ETS`，也可以启动后在首页手动选择目录）。
3. 当前 Windows 用户对 `%APPDATA%\ETS` 有读写权限。
4. 自动答题需要 ETS 版本与助手兼容，并允许助手连接本机 ETS。
5. `_winui` 已随附 VC++ 2015–2022 运行库；如果目标系统有额外的策略限制，仍可能需要安装官方 VC++ 运行库。

如果启动时报 `No module named '_socket'`，请检查 `_internal_engine\_socket.pyd` 和该目录下的 `python3*.dll`。旧版仓库的 Git 忽略规则误排除了全部 `.pyd` 原生扩展，导致 GitHub 下载包缺文件；请重新下载修复后的完整程序包并完整解压，保留与 EXE 配套的整个 `_internal_engine` 目录。构建时也会检查发布文件是否被 Git 忽略。若新版解压后仍缺文件，再检查安全软件的隔离记录。

直接双击：

`_winui\ETS-Assistant-WinUI.exe`

首次使用点击“初始化并打开 ETS”。程序会创建桌面快捷方式和当前用户的登录启动项，并启动后台监听器。以后打开 ETS 时会自动启动监听、转换和答题进程。

## 界面与设置

- **运行概览**：查看 ETS、监听器、凭据和当前试卷状态；快速初始化并启动 ETS、打开 ETS、手动重置或结束 ETS 进程；查看当前试卷答案和答题进度。
- **试卷管理**：查看本机下载的试卷，打开试卷目录、提取答案或删除试卷；配置 DeepSeek 凭据或启用体验模式后，可确认并生成逐题解析 PDF。
- **答题设置**：设置目标正确率、跳题间隔、Windows 登录时启动监听器、口语模式和自动听力；展开“服务凭据”配置 ETS 安装目录、DeepSeek API Key、阿里云 TTS 凭据或导入 CSV。
- **当前活动**：查看进行中的试卷解析任务及最近完成的任务；多个解析任务可以并行运行。
- **偏好设置**：设置体验模式、查看听力/口语/解析/TTS 剩余用量，以及启用或手动检查 GitHub 更新。
- **开发者工具**：默认隐藏；在“关于”页连续点击版本号 7 次并确认后显示，提供诊断、日志、抓包和虚拟麦克风相关工具。
- **使用说明与关于**：查看本机使用说明、版本和项目链接。

配置和界面偏好保存在 `%APPDATA%\ETS`。桌面快捷方式和登录启动项只影响当前 Windows 用户，不修改 ETS 安装目录中的厂商文件。

GitHub 更新检查只比较三段产品版本（例如 `1.1.0`）。相同版本的仓库代码和 Release MSI 视为同一 build；提交时间、版本修订号或构建元数据变化不会单独触发更新提示。

## Cloudflare 免费服务

免密 Worker 地址 `https://ets.azursmelt.us.ci` 已内置在程序中，无需填写。没有本机凭据时，听力、DeepSeek 口语生成、试卷详细解析和阿里云 TTS 都会自动走 Worker；每项服务分别按每台设备每天最多 10 次、同一来源 IP 每天最多 30 次计数，按北京时间 00:00 重置。口语生成每次生成、完整试卷解析每份试卷各计 1 次；TTS 每次完整回答合成计 1 次（长文本分段仍算一次）。DeepSeek Key 和阿里云 AccessKey/AppKey 只保存在 Worker Secret 中；填写本机凭据后则直连服务商，不占用 Worker 额度。听力原文、口语题干/提示词、试卷题目/答案和 TTS 文本会发送给对应服务。部署步骤见 [`backend/cloudflare-worker/README.md`](backend/cloudflare-worker/README.md)。

## DeepSeek 口语测试

在“答题设置”页填入 DeepSeek API Key 并保存，密钥会用 Windows DPAPI 加密后存放在 `%APPDATA%\ETS\deepseek_api_key.bin`，不会写入配置文件或仓库。

同一页支持 CSV 批量凭据导入，可一次写入 DeepSeek 和阿里云 TTS 凭据：

```csv
type,api_key,access_key_id,access_key_secret,app_key
deepseek,sk-xxxx,,,
aliyun,,LTAIxxxx,secret,appkey
```

也支持无表头格式：

```csv
deepseek,sk-xxxx
aliyun,LTAIxxxx,secret,appkey
```

导入后会分别使用 Windows DPAPI 加密保存，界面不显示明文，程序也不提供导出功能；原始 CSV 文件不会被复制进程序目录，如包含明文密钥请自行妥善保管或删除。

同一页还可以切换口语模式：

- **标答模式**：直接从试卷包的朗读/看图/问答数据里提取推荐答案（与转换后 md 中的 `Model answer`、`Reference text` 同源），不调用 DeepSeek，适合直接送 TTS。
- **灵活模式**：调用 `deepseek-flash`，每次请求附带题干和 `llm_stt.md` 生成回答。

“测试工具”页的“DeepSeek 口语测试”卡片可以输入题干并发送：每次请求都会把题干和 `llm_stt.md` 附件一起发给 `deepseek-flash`，返回的正文显示在下方，可直接复制。附件优先从程序目录、`_winui\Assets` 读取，找不到时回退到 `%USERPROFILE%\Downloads\llm_stt.md`。

点击“读取所有试卷口语题”会像试卷列表一样扫描本机所有试卷包，列出其中有朗读、看图说话或问答类题目的试卷；先在试卷下拉框选一份，再选题。标答模式直接显示推荐答案，灵活模式则把题干送到 DeepSeek。

## 阿里云 TTS 测试

“测试工具”开发者页仍可填写个人 RAM AccessKey ID、AccessKey Secret 和智能语音交互项目 AppKey。本机凭据只保存在主程序 EXE 旁边的 `aliyun_tts_credentials.bin` 并通过当前 Windows 用户 DPAPI 加密；没有本机凭据时自动使用内置 Worker，并按上文每日限额运行。程序不会访问 Windows 凭据管理器、`%APPDATA%` 或 `%LOCALAPPDATA%` 中的凭据副本，也不会自动迁移旧凭据。建议只使用权限受限的 RAM 用户凭证。

AccessKey ID/Secret 是用于认证的长期凭证；阿里云不提供长期有效的 NLS AccessToken。助手通过阿里云 SDK 调用 `CreateToken`，在进程内缓存 Token，并依据返回的 `ExpireTime` 在到期前重新获取。Token 和 AccessKey Secret 不写日志。

“测试工具”页的阿里云 TTS 按钮会把当前口语回答使用标准 TTS（Betty 美式英文女声、WAV 16 kHz）合成并打开播放。超过 300 个字符的回答会按句子/空格边界分段合成，再拼接为一个 WAV；文件保存在 ETS 程序根目录下的 `temp\tts-test`。长文本 TTS 和 CosyVoice 流式合成不在此测试流程中。

## 语音注入（虚拟麦克风）

“测试工具”页的“语音注入（虚拟麦克风）”卡片用于把 TTS 生成的 WAV 送进 ETS：

1. 合成 TTS 后点“使用最近 TTS WAV”，或点“选择 WAV 并准备”。助手会用 ETS 自带的 ffmpeg 把它转成 16-bit / 48 kHz，写到 `%APPDATA%\ETS\_assistant\speaking_input.wav`。
2. 完全关闭 ETS（包括托盘进程），点“用虚拟麦克风启动 ETS”，在 UAC 提示中允许。助手会用 Chromium 的 `--use-fake-device-for-media-stream` / `--use-file-for-fake-audio-capture` 参数启动 ETS，ETS 录音时会把这个 WAV 当作麦克风输入。
3. 默认只播放一次（`%noloop`）；勾选“循环播放 WAV”可循环。

限制：ETS 必须带这些参数启动，所以每次换 WAV 都要重启 ETS；试卷监听不受影响。如果 ETS 没有转发这些 Chromium 参数（表现为录音仍来自真实麦克风），可以改用虚拟声卡（VB-CABLE / VoiceMeeter）：把播放设备设为 CABLE Input，ETS 麦克风设为 CABLE Output，再把 WAV 播放到 CABLE Input。

## 整卷口语音频

“测试工具”的“整卷口语音频（预生成 → 播放）”卡片把整份口语卷先准备好：

1. 先点“读取所有试卷口语题”，在试卷下拉框选择试卷。
2. 点“预生成整卷 TTS”：按当前口语模式（标答/灵活）逐题生成答案，依次调用阿里云 TTS，全部完成后写入 `%APPDATA%\ETS\_assistant\speaking_session.json`，并显示在播放列表里。
3. 安装 VB-CABLE 后，在 Windows 声音设置里把 ETS 使用的麦克风设为 `CABLE Output (VB-Audio Virtual Cable)`。
4. 口语题开始录音时，点“播放下一题”，助手会把对应 WAV 播放到 `CABLE Input`（声音设置里显示为“扬声器 (VB-Audio Virtual Cable)”）。

已实测 VB-CABLE 的播放→录音通路：向虚拟声卡播放 1 kHz 测试音，从 CABLE Output 录到的峰值一致。

## 完全重置

“测试工具”页底部的“完全重置”会停止监听器、转换器和答题进程，删除登录启动项、桌面与启动快捷方式，清除 DeepSeek/阿里云凭证和界面偏好，删除虚拟麦克风 WAV、TTS 临时文件与助手日志。试卷包、答题历史和答案记录不受影响；虚拟声卡驱动不会被自动卸载，需要时请在设备管理器或“应用和功能”里手动移除。

## 重新构建（开发用）

运行不需要 Python；重新打包才需要：

- Python 3.10+（当前使用 3.14）
- `pip install -r requirements-build.txt`（PyInstaller、Pillow、reportlab）
- .NET 8 SDK
- 桌面程序构建完成后运行 build_release_package.ps1，生成 MSI 与便携 ZIP，保存在 .build\versions\<版本号>\release。
- 运行 `build_ets_assistant_winui3.ps1`

本地构建工具和缓存统一放在 `.build\tools`，不纳入仓库。构建脚本会优先使用该目录中的 .NET SDK 和 Python 依赖；缺失时再使用系统 Python/.NET。旧版发布包和中间产物按项目版本归档在 `.build\versions\<版本号>`。

## 数据与排错

试卷包和日志位于 `%APPDATA%\ETS`。主要日志：

- `ets_answer_listener.log`
- `ets_auto_answer.log`
- `ets_converter.log`
- `ets_winui_startup.log`
- `ets_packet_capture.log`（启用抓包模式后生成）

ETS 已打开但助手显示未连接时，先关闭 ETS，再通过助手重新打开；仍未连接时检查 ETS 安装目录是否选择正确。程序不会点击最终提交按钮，是否删除已提交试卷包由用户确认。

## 运行位置与权限

监听器需要写入 `%APPDATA%\ETS`（日志和答题历史）。如果程序目录位于 Windows 受限位置，Windows 可能会以低完整性级别启动程序，导致监听器无法写入数据。此时请把运行目录放到普通用户目录（例如 `%LOCALAPPDATA%\Programs\ETS-Assistant`），并让桌面快捷方式和登录启动项指向该目录。

## 版本历史

## 1.1.2 正式版（2026-10-26）
- 修正虚拟声卡未配置提示，改为引导到“答题设置”。
- 正式版启用偏好设置中的自动与手动更新检查。

## 1.1.1 正式版（2026-10-25）
- 答题设置新增 VB-CABLE 驱动状态、安装和卸载入口；操作通过随包官方设置程序并请求管理员权限。
- 卸载设置程序关闭后，提示是否立即重启以完成驱动移除。
- 自动口语页的驱动安装入口移至答题设置，修正缺少驱动时的引导提示。
- 发布 MSI 安装包和便携 ZIP。
## 1.1.0 正式版（2026-10-25）
- 模拟考试中，每道听力题的 TTS 音频全部生成成功后自动启动自动口语；运行器等待 ETS 进入录音阶段再播放。
- 修复关闭 ETS Shell 后虚拟音频设备仍为默认设备的问题；Shell 退出时恢复此前的物理麦克风和扬声器。
- 移除提交答题后的清理确认与自动删除，已提交的试卷包和转换文件会保留。
- 新增 WiX MSI 和便携 ZIP 的可复用发布打包流程；MSI 支持选择安装目录并创建开始菜单快捷方式，卸载时保留用户数据。VB-CABLE 仍可从应用工具页按需安装。


## 1.0.1 构建更新（2026-10-25）
- 口语 TTS 全部生成成功后自动启动“自动口语”，由运行器等待 ETS 进入录音阶段；生成失败时不会启动
- 移除答题提交后的试卷清理弹窗；自动答题不再提示或删除已提交的试卷包和转换文件
- 修复关闭 ETS Shell 后默认音频仍停留在虚拟设备的问题；检测到 ETS Shell 退出后恢复此前的物理麦克风和扬声器
- 修复模拟考试中开始自动口语后立即显示“已结束”：在准备时间等待，在 ETS 显示录音时播放对应 TTS，并等待 ETS 自动结束本题后继续
- 修复中途退出并重进同一试卷时口语准备窗口不再出现的问题；窗口重新打开后会复用仍有效的音频，避免重复调用 DeepSeek/TTS

## 1.0.1 正式版（2026-10-24）
- 窗口模式下折叠导航按钮使用半透明背景；主窗口最小宽度设为 450 DIP
- 修复窄窗口下首页“一键初始化并启动”按钮被压成小块的问题，改为占满操作区整行
- 窄窗口下页面标题避开导航菜单按钮；试卷列表合并日期列并为操作按钮保留空间
- 非最大化窗口下导航栏自动切换为紧凑模式，页面边距随可用宽度收缩
- 当前活动无任务时显示与侧栏相同的同步图标；首页操作区较窄时将启动按钮移到下一行

## beta 1.8.8（2026-10-24）
- DeepSeek 与阿里云 TTS 的完整个人凭据配置后，体验模式自动关闭并禁用；清除凭据后可重新手动启用。

## beta 1.8.7（2026-10-24）

- 体验模式下，有效的整卷解析请求受理后立即扣 1 次试卷解析额度；DeepSeek 上游失败不退还，并在错误提示中说明。

## beta 1.8.6（2026-10-24）

- 体验模式可通过 DeepSeek 公用 Worker 生成整卷逐题解析；每次完整解析扣 1 次独立额度，Worker 内部拆分批次不重复扣额
- 试卷管理在体验模式开启后也显示详细解析按钮；确认前说明发送内容与额度规则
- 偏好设置用量卡片新增“试卷解析”，分别展示设备与公网 IP 剩余次数

## beta 1.8.5（2026-10-24）

- 修复体验模式开启且未配置阿里云凭据时，整卷 TTS 预生成仍被本地凭据检查拦截的问题；现在会调用公用 Worker 并按设备/IP 日额度计数。

## beta 1.8.4（2026-10-24）

- 偏好设置在体验模式开启时显示听力、口语、TTS 的设备与当前 IP 剩余额度，并可手动刷新；新增不扣额度的 Worker 用量查询接口

## beta 1.8.3（2026-10-24）
- DeepSeek 详细解析 PDF 按 Markdown 标题、列表、引用、表格和强调样式排版，输出结构更清晰
- 仅在试卷实际包含口语题干时，才把“口语题干”字段加入发送给 DeepSeek 的提示词
- 详细解析固定保存到 Windows 下载文件夹，每次生成和预览都显示完整保存路径
- PDF 预览增加“用默认方式打开 PDF”按钮
- 新增当前活动页面，以圆形进度指示正在生成的解析；不同试卷的解析可以并行生成

## beta 1.8.2（2026-10-23）

- 首页四个启动操作改为同一行的普通高度按钮，补齐首页与凭据区按钮图标，并移除首页 README 入口。
- BETA 徽标恢复小圆角矩形条；听力说明与卡片内容左边缘对齐。
- 凭据折叠栏增高到 88，并修正 Expander 的内容呈现对齐，使内层卡片撑满可用宽度。

## beta 1.8.1（2026-10-23）

- 调整首页按钮排布，为打开 ETS 按钮添加图标。
- 自动听力 Beta 改为胶囊徽标，描述文本左对齐；答题设置与凭据区统一卡片圆角和宽度。
- 增加凭据表单留白和输入控件高度，并移除开发者工具中重复的口语模式设置。

## beta 1.8.0（2026-10-23）

- 首次启动引导填写 DeepSeek 与阿里云 TTS 凭据；体验模式改为明确选择，公用 Worker 有设备/IP 日限额。
- ETS Tool 运行期间可临时将 VB-CABLE 设为默认录音设备，关闭程序时恢复之前的默认输入。
- 首页改为快速开始布局；答题设置收纳到折叠凭据区，随机种子自动生成，并增加自动听力 Beta 开关。
- 试卷管理可在确认后将题干、选项和答案发送到 DeepSeek，生成 PDF 并在应用中预览。
- 开发者工具需在“关于”连续点击版本号 7 次并确认后显示；偏好设置增加 GitHub Release/提交更新提示。

