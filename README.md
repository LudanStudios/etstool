# ETS 自动监听配置指南（新手版）

这份指南按顺序说明：新电脑要准备什么、每一步要点哪里、需要输入什么，以及怎么确认程序已经启动。照着做时，先完成“首次配置”，之后日常使用只需看“每天怎么启动”。

## 先认识这几个文件

请把以下文件放在同一个目录：`%APPDATA%\ETS`（通常对应 `C:\Users\你的用户名\AppData\Roaming\ETS`）。

| 文件 | 用途 |
|---|---|
| `exam_pack_to_markdown_pdf.py` | 发现新试卷包后解析题目和答案，输出 Markdown；可选生成 PDF。 |
| `ets_answer_listener.py` | 等待 ETS 启动，并负责启动或停止另外两个脚本。 |
| `ets_auto_answer.py` | 读取本机答案，通过 ETS 页面接口操作选择题。 |
| `setup_ets_autolistener.ps1` | 首次配置登录启动项和桌面快捷方式。 |

还需要把要用的**完整试卷包文件夹**放在这个目录下。试卷包文件夹名称以数字开头，里面应保留原来的 JSON 文件和目录结构，例如：

```text
ETS\
├─ exam_pack_to_markdown_pdf.py
├─ ets_answer_listener.py
├─ ets_auto_answer.py
├─ setup_ets_autolistener.ps1
└─ 216809\
   └─ ...\content.json
```

不要只复制 `content.json`；请复制完整的数字开头试卷包文件夹。

## 新电脑首次配置

### 第一步：安装 Python

1. 在新电脑上安装 Python **3.10 或更新版本**。
2. 安装时如果出现 **Add Python to PATH** 选项，请勾选它，再继续安装。
3. 安装完成后，按键盘上的 `Win` 键，输入 `PowerShell`，点击打开 **Windows PowerShell** 或 **PowerShell 7**。
4. 在窗口中输入下面一行，然后按 `Enter`：

   ```powershell
   python --version
   ```

5. 如果显示类似 `Python 3.14.0` 的版本号，继续下一步。如果显示“找不到命令”或类似错误，说明 Python 没有正确安装或没有加入 PATH；先重新安装并勾选 **Add Python to PATH**。

### 第二步：把文件复制到 ETS 文件夹

1. 按 `Win + E` 打开文件资源管理器。
2. 点击窗口顶部的地址栏，输入下面这段路径，然后按 `Enter`：

   ```text
   %APPDATA%\ETS
   ```

3. 如果 Windows 询问是否创建文件夹，选择“是”。
4. 把上面列出的四个文件和需要使用的完整数字开头试卷包文件夹复制到这里。
5. 检查一下：四个文件应直接位于 `ETS` 文件夹中，试卷包也应直接放在 `ETS` 文件夹中，不要多套一层同名文件夹。

### 第三步：运行一次初始化脚本

1. 按 `Win` 键，输入 `PowerShell`，打开 PowerShell。
2. 复制下面第一行，粘贴到 PowerShell 窗口，再按 `Enter`：

   ```powershell
   Set-Location "$env:APPDATA\ETS"
   ```

3. 再复制下面一行，粘贴后按 `Enter`：

   ```powershell
   .\setup_ets_autolistener.ps1
   ```

4. 等待脚本运行结束并阅读屏幕上的提示。若提示缺少文件、Python 或找不到 ETS，请按提示修正后再运行一次。
5. 初始化成功后，桌面上应出现 **ETS Answer Listener** 快捷方式。这个步骤只配置当前 Windows 用户的登录启动项，并创建桌面快捷方式；不会修改 `Program Files` 中的 ETS 安装文件。

如果 PowerShell 提示“禁止运行脚本”，先不要执行网上随意找到的解除限制命令。把完整错误文字复制下来检查；有些电脑由组织策略管理，需按该电脑的策略处理。

### 第四步：从桌面快捷方式打开 ETS

1. 如果 ETS 已经开着，请先正常关闭它。
2. 回到桌面，双击 **ETS Answer Listener** 快捷方式。
3. 等 ETS 完全打开。监听器检测到 ETS 后，应该会弹出一个命令窗口，显示试卷包转换和答案输出信息。

初始化脚本还会设置登录启动项：下次登录 Windows 时，监听器会在后台等待 ETS。ETS 关闭后，转换和自动答题子进程会结束；监听器继续等待 ETS 再次启动。

## 检查自动答题所需的接口

自动答题要求 ETS 在本机开放调试接口。ETS 打开后，按 `Win + R`，在“运行”框里输入下面的网址，再按 `Enter`：

```text
http://127.0.0.1:10086/json/version
```

如果浏览器显示一段包含版本信息的文本或 JSON，接口可访问。如果显示无法连接、拒绝连接或网页打不开，自动答题脚本就无法连接 ETS。

桌面快捷方式会尝试用 `--remote-debugging-port=10086` 参数启动 ETS，但不同 ETS 版本可能不接受这个参数。若接口打不开：

1. 确认 ETS 是通过桌面上的 **ETS Answer Listener** 快捷方式启动的。
2. 关闭 ETS 后重新双击该快捷方式，再检查上面的网址。
3. 仍然打不开时，当前 ETS 版本可能不支持这种启动方式。初始化脚本不会修改 ETS 安装文件或厂商配置；需要找到该版本支持的调试接口配置后才能使用自动填答。

## 每天怎么启动

1. 登录 Windows。
2. 双击桌面的 **ETS Answer Listener** 快捷方式打开 ETS。
3. 等待命令窗口显示监听/答案信息。
4. 如果命令窗口没有出现，先确认监听器正在运行；也可以按下面“手动启动监听器”的步骤查看错误。

若 ETS 是从其他图标或快捷方式启动的，可能没有带上调试端口参数。请使用 **ETS Answer Listener** 快捷方式启动。

## 手动启动监听器（排错用）

1. 按 `Win` 键，输入 `PowerShell`，打开窗口。
2. 输入下面两行，每行输入后按 `Enter`：

   ```powershell
   Set-Location "$env:APPDATA\ETS"
   python .\ets_answer_listener.py
   ```

3. 让这个 PowerShell 窗口保持打开，然后再启动 ETS。窗口里如果出现错误信息，可以复制下来用于排查。
4. 手动运行后要停止监听器时，先让 PowerShell 窗口成为当前窗口，再按 `Ctrl + C`。

## 试卷包与答案匹配

脚本会读取数字开头试卷包文件夹里的 JSON。目录中的试卷 ID 必须和 ETS 当前题目对应；选择题数据需要包含脚本能识别的选择题结构（例如 `structure_type=collector.choose`）和 A、B、C、D 答案。

转换器的 `--watch` 模式只处理它开始监听后**新出现**的数字开头文件夹。监听开始前已经存在的文件夹会跳过。如果要处理旧试卷包，可在 PowerShell 里手动运行：

```powershell
Set-Location "$env:APPDATA\ETS"
python .\exam_pack_to_markdown_pdf.py --help
```

按屏幕显示的用法，为已有试卷包运行一次转换命令。

## PDF（可选）

没有 PDF 库时，Markdown 和答案输出仍可使用，只是不会生成 PDF。若需要 PDF：

1. 打开 PowerShell。
2. 输入下面命令并按 `Enter`：

   ```powershell
   python -m pip install reportlab pillow
   ```

## 常见问题

### 桌面没有出现快捷方式

回到 PowerShell，按“第三步”重新运行初始化脚本。确认脚本没有报错，并确认 `setup_ets_autolistener.ps1` 就在 `%APPDATA%\ETS` 目录中。

### 登录后没有命令窗口

命令窗口通常在 ETS 启动后才出现。先双击 **ETS Answer Listener** 桌面快捷方式启动 ETS。如果仍没有窗口，按“手动启动监听器”操作并查看 PowerShell 输出。

### ETS 打开了，但没有自动填答案

按顺序检查：

1. 是否通过 **ETS Answer Listener** 快捷方式启动 ETS。
2. `http://127.0.0.1:10086/json/version` 是否能打开并显示版本信息。
3. 完整试卷包是否放在 `%APPDATA%\ETS` 下。
4. 试卷包 ID 是否和 ETS 当前题目对应，JSON 中是否有可识别的选择题答案。

### 缺少 PDF 依赖

这是可选功能。若不需要 PDF，可以忽略提示；需要 PDF 时按上面的安装步骤操作。

## 功能范围

- 当前自动操作面向 ETS 页面中的选择题。
- 脚本可以选择答案并前进题目；不会主动点击 ETS 的最终提交按钮。
- 自动答题依赖本机 `127.0.0.1:10086` 调试接口。接口是否能由启动参数开启，取决于安装的 ETS 版本。
