# OpenETS iOS 客户端 —— API 协议分析报告

> 分析对象：`OpenETS celespirit@icloud.com.xcarchive.zip`
> 分析方式：Mach-O 反汇编 + dSYM（DWARF v5）+ Swift 运行时元数据 + 字符串表静态分析
> 结论标注：**✅ 确证**（代码/常量直接可见） · **⚠️ 推断**（由调用序列与字段推断，未抓包验证）

---

## 0. 样本基本信息

| 项目 | 值 |
|---|---|
| Bundle ID | `com.nexus.OpenETS` |
| 版本 | 1.0 (Build 1) |
| 最低系统 | iOS 27.0，arm64 |
| 构建环境 | Xcode 27.2 (DTXcode 2720)，SDK iphoneos27.2 |
| 主二进制 | `OpenETS`，1,736,112 字节，Swift 6 + SwiftUI |
| 调试符号 | 完整 dSYM（DWARF：5,348 个函数、9,605 个类型、全部字段名） |
| 签名 | 个人团队 `R7FL9PU53M`（TeamName 为中文个人名），1 台设备，`get-task-allow=true` |
| 描述文件到期 | **2026-10-22**（明天到期） |
| ATS | ❗Info.plist **没有** `NSAppTransportSecurity` 例外 → 全站强制 HTTPS/WSS |

Swift 源码文件结构（从 dSYM 还原，共 47 个 .swift）：

```
OpenETS/Services/     ETSAuthentication.swift  ETSExamClient.swift  ETSReadClient.swift
                      ETSRecordingUploader.swift  ETSRequestEnvelope.swift
                      ETSSessionStore.swift  ETSSpeechService.swift  ETSSpeechWire.swift  ETSSpeechXML.swift
OpenETS/Compatibility/ ETSExamDecoder.swift  ETSJSONValue.swift  ETSControlScript.swift
OpenETS/Exam/          ExamAttempt ExamDisplay ExamMedia ExamQuestion ExamRecorder ExamResources ExamRunner ExamSubmissionCoordinator
OpenETS/Tools/         ETSAPIDiagnostics APIActivityView APIDashboardView APITestDraft
```

---

## 1. 网络层总览

### 1.1 服务器地址 ✅

```text
HTTP  基址   https://api.ets100.com/                    （+ route 直接拼接）
完整 URL     https://api.ets100.com/point-service/homework/rank/list
WebSocket    wss://oe-pub.eduaiplat.com/ws/v2/ise
```

### 1.2 请求形态 ✅

`ETSExamClient.send` / `ETSReadClient.request` / `ETSAuthenticationClient.send` 三者实现一致：

1. `URL(string: "https://api.ets100.com/" + route.rawValue)`
2. `URLRequest(url:cachePolicy:timeoutInterval:)`
3. `request.httpMethod = "POST"`
4. `setValue("application/json", forHTTPHeaderField: "Content-Type")`
5. `JSONEncoder().encode(envelope)` → `request.httpBody`
6. `URLSession.data(for:)` → 记录到 `ETSAPIDiagnostics` → `ETSResponseDecoder.body`
7. 错误分类：`sessionExpired / unavailable / rejected / oversizedResponse / unexpectedHTTPStatus`

读取客户端还限制响应体大小：`ETSReadClient.maximumResponseBytes`（超过即 `oversizedResponse`）。

### 1.3 请求信封 ETSRequestEnvelope ✅

```swift
struct ETSRequestEnvelope {
    var body: String        // 业务参数 JSON 的“字符串”（二次序列化）
    var head: Header
}
struct Header {
    var version: String      // 客户端版本
    var sign: String         // 签名
    var pid: String          // 协议/产品标识（来自 profile.pid）
    var time: String         // "yyyy-MM-dd HH:mm:ss"
    var time_second: Int     // Unix 秒
}
```

对应 JSON（字段名由 `Header.CodingKeys.stringValue` 反汇编确认 ✅）：

```json
{
  "body": "{\"homework_id\":\"...\",\"...\":\"...\"}",
  "head": {
    "version": "1.0",
    "sign": "8F3C...",
    "pid": "...",
    "time": "2026-10-21 23:45:00",
    "time_second": 1792626300
  }
}
```

> 关键点：**body 是 JSON 字符串**（被转义后放进信封），不是嵌套对象。

### 1.4 响应形态 ✅（`ETSResponseDecoder.body` 反汇编确认）

响应不是普通对象，而是 **只含 1 个元素的 JSON 数组**：

```json
[ { "code": 20000, "body": { ...业务数据... } } ]
```

- 解码为 `[ETSJSONValue]`，**强制校验数组 count == 1**，否则抛 `response envelope`
- 元素内先取 `code`（数值，`requiredNumber("code")`），再按 `code` 分支；成功分支取 `body`
- `code` 的判等常量经反汇编为 **20000.0** ⚠️（建议抓一次成功响应核对）
- 非成功码 → `ETSServiceError`，映射为 `sessionExpired`（重新登录）/ `unavailable` / `rejected` 等

---

## 2. 签名算法（本项目最核心的部分）

### 2.1 配置对象 ETSProtocolProfile ✅

Swift 反射元数据精确还原（4 个字段，均为 `String`）：

| 字段 | 含义 |
|---|---|
| `k_serves` | 服务标识 |
| `requestSecretBase64` | **Base64 编码的签名密钥** |
| `pid` | 产品标识，写入 `head.pid` |
| `clientVersion` | 客户端版本，写入 `head.version` |

`validate()` 会校验四项非空，否则抛 `compatibility profile` 错误 —— 说明作者把协议密钥做成了**外部可配置项**，二进制里**没有内置默认值**（已验证：全量字符串表中不存在可用 base64 密钥）。

### 2.2 签名流程 ETSNativeRequestSigner.sign(body:timestamp:) ✅（顺序为 ⚠️推断）

反汇编得到的调用序列：

```text
ETSProtocolProfile.validate()
String.append(body)                     // payload = body
String.append(timestamp)                // payload += timestamp
Data(base64Encoded: requestSecretBase64) // 密钥字节
Data.append(payload.utf8)               // 密钥字节 + payload
CryptoKit.Insecure.MD5.hash(data:)      // MD5
map { ... }.joined(separator: "")       // 十六进制（字符串表含 "0123456789ABCDEF"）
ETSRequestSignature(pid: profile.pid, digest: hex)
```

即：

```text
sign = HEX( MD5( base64_decode(requestSecretBase64) || body || timestamp ) )
```

- 使用 **MD5**（`CryptoKit.Insecure.MD5`）✅
- 十六进制查表 `0123456789ABCDEF` → 大概率为**大写** ⚠️
- `timestamp` 即 `head.time` 字符串（服务端需按同一格式重算）
- ⚠️ 三段拼接顺序建议用一次真实请求做差分验证（改动 body 一个字节看 sign 变化不够，需构造离线校验）

### 2.3 凭据“加密” ETSProtocolProfile.encryptCredential ✅（弱加密）

登录时密码走 `encryptCredential`：`String → Data → … → Data.base64EncodedString(options:)`。
反汇编范围内**没有任何 CryptoKit AES/RSA/HMAC 调用**，只有 Data 逐字节处理 + Base64 编码 → 属于**混淆级**处理（逐字节 XOR/替换后 Base64），并非真正的加密 ⚠️。

---

## 3. 端点清单（全部静态还原）✅

路由枚举的 `rawValue` 由 switch + 内联小字符串（≤15 字节的 Swift small-string，编译器用 `movz/movk` 立即数构造，不在 `__cstring` 里）拼出，需反汇编才能拿全。

### 3.1 认证 `ETSAuthenticationClient`

| 路由 | 用途 | 备注 |
|---|---|---|
| `user/login` | 手机号+密码登录 | 入参含 `password`(encryptCredential 处理)、手机号、deviceID；响应 → `ETSSession` |
| `user/rebind-code` | 账号重绑定/换绑 | 出现在登录后的分支 |

登录失败提示原文：`ETS could not complete sign in (code …). Device verification or account confirmation may be required in the original client.`
→ 说明第三方客户端在**风控/设备校验**环节会被拦截。

### 3.2 只读接口 `ETSReadRoute`（10 个路由：4 个在 `__cstring`，6 个为内联小字符串）

| # | 路由 | 推测用途 |
|---|---|---|
| 1 | `g/homework/list` | 作业列表（分页）✅ 与 `SchoolworkPage` 对应 |
| 2 | `g/homework/info` | 单个作业详情 |
| 3 | `m/homework/struct` | 作业/试卷结构（模板、区块、控制脚本） |
| 4 | `g/homework/score-detail` | 已完成作业的成绩明细 |
| 5 | `m/class/list` | 班级列表 |
| 6 | `m/ecard/list` | 学习卡/电子卡列表 |
| 7 | `system/config` | 系统配置 |
| 8 | `p1/student/config/config/ossEvaluateUploadStsToken` | **OSS 直传临时凭证（STS）** |
| 9 | `ai/aiplat/getaitoken` | **语音评测 AI Token** |
| 10 | `g/set/ppr-res` | 试卷资源（朗读题资源） |

> 另有一条**独立完整 URL**：`https://api.ets100.com/point-service/homework/rank/list` —— 优秀榜/排名（`ETSReadClient.excellenceBoard`），走 `point-service` 命名空间。

### 3.3 考试写入接口 `ETSExamRoute`（4 个路由）

| 路由 | 用途 |
|---|---|
| `g/homework/checkstart` | 开考校验（权限、次数、逾期、是否已提交） |
| `m/homework/set-use-time` | 上报用时/开始时间 |
| `m/homework/submit` | **提交答案** |
| `m/audio/sync-v2` | 录音/音频同步登记（v2） |

### 3.4 端口使用矩阵 ⚠️

`ETSExamClient.send` 内部按枚举分支拼接三种路由（`m/homework/submit` / `m/homework/set-use-time` / `g/homework/checkstart` / 内联的 `m/audio/sync-v2`），`ETSReadClient.request` 处理其余只读路由。

---

## 4. 会话与账号

### 4.1 ETSSession ✅

```swift
struct ETSSession {          // Keychain 持久化（ETSSessionStore，service/account 键）
    let accountID: String
    let token: String        // 后续请求的鉴权 token
    let clientVersion: String
    let parentAccountID: String?   // 家长账号（家长端联动）
}
```

存储实现：`SecItem*` + `kSecReturnData`（Keychain），另有 `loadCredentials/saveCredentials` 保存 `ETSCredentials{phone,password}`。

### 4.2 列表请求参数 ✅（`SchoolworkLibrary.fetch` 内联构建）

```json
{
  "parent_account_id": "...",
  "show_old_homework": "1",
  "get_to_overtime_count": "1",
  "school_level_filter": "...",
  "use_homework_privilege": "...",
  "recent_account_id": "...",
  "status": "...",
  "offset": 0,
  "limit": 20
}
```

响应 `SchoolworkPage`：

```swift
struct SchoolworkPage {
    var assignments: [SchoolworkAssignment]
    var totalCount: Int?
    var toDoCount: Int?
    var overdueCount: Int?
    var serverDate: Date
    var metadata: [String: ETSJSONValue]   // "to_overtime_count" 等原始字段
}
struct SchoolworkAssignment {
    var id, title: String
    var group: Group
    var completionPercentage: Double?
    var beginsAt, endsAt: Date
    var statusCode: Int?
    var metadata: [String: ETSJSONValue]
}
```

---

## 5. 试卷结构与题型（答题核心）

### 5.1 结构模型 ✅

```swift
ETSExamTemplate { id, sections: [ETSExamSection], steps: [ETSControlStep], metadata }
ETSExamSection  { order, title, instructions, blocks: [ETSExamBlock], metadata }
ETSExamBlock    { id, order, contentID, questionMetadata: [[String: ETSJSONValue]], metadata }
ETSContent      { id, collector, fields: [String: ETSJSONValue] }
ETSControlStep  { order, subjectID, contentID, hint, synchronization, action, metadata }
```

### 5.2 采集器/题型 ✅（`collector.*`）

| collector | 题型 |
|---|---|
| `collector.choose` / `choose` | 选择题（听/读） |
| `collector.word` | 单词朗读 |
| `collector.read` | 句子/段落朗读 |
| `collector.picture` | 看图说话 |
| `collector.dialogue` | 情景对话 |

作答模型：

```swift
ExamPrompt  { displayNumber, entityID, order, collector, title, text, choices: [ExamChoice], images }
ExamChoice  { id, label, text, image? }
ExamQuestion{ prompt, reference{answer, explanation, transcript}, maximumScore, questionTypeMaximumScore, evaluationType, metadata }
```

### 5.3 判定规则 ✅（内置文案可反推服务端字段）

- `read_write`、`composition`、`audio_task` —— 考试类型
- `to_overtime_count`、`get_overtime_count`、`show_score_answer`、`show_old_homework`
- `pc_accept_reject_score` —— 语音评测接受/拒绝阈值
- 本地拦截文案：只允许一次、未开启补交、逾期已交、已过期不可重做

---

## 6. 提交链路（`ETSExamPayloads` + `ExamSubmissionCoordinator`）

### 6.1 分阶段提交 ✅

`ExamSubmissionCoordinator.submit` 与 `ExamAttempt.phase` 显示状态机：

```text
ready → running → interrupted → finished → uploading → finalizing → submitted
                                          └→ uncertain（结果未知，需先核对再重发）
```

### 6.2 载荷字段 ✅（内联小字符串反汇编所得，前缀已确认）

每题（`ETSExamPayloads.answer`）：

```text
answer / question_id / choose_id / total_score / real_score / rate_scale
resource…（录音资源引用） / homework…（homework_id） / use_time
时间戳格式："yyyy-MM-dd HH:mm:ss"
```

- `ETSExamPayloads.elapsed` —— 用时上报（对应 `m/homework/set-use-time`）
- `ETSExamPayloads.finalization` —— 结束确认（对应 `m/homework/submit` 的收尾）

### 6.3 本地应答模型 ✅

```swift
ExamResponse { questionID, choiceID?, recording?, grade: [String: ETSJSONValue] }
QuestionScore{ questionID?, itemID?, score, maximumScore, answer, audioReference, metadata }
CompletedExamResult { score, maximumScore, questionScores, speechDimensions, stars, ... }
```

---

## 7. 录音上传（OSS 直传）✅

两步式，与阿里云 OSS 表单直传完全一致：

```text
① POST https://api.ets100.com/p1/student/config/config/ossEvaluateUploadStsToken
   → { accessKeyId, policy, signature, securityToken/expiration, endpoint, dir, ... }

② POST <endpoint>   multipart/form-data
   OSSAccessKeyId, policy, signature, key, success_action_status,
   x-oss-security-token, x-oss-object-acl, Content-Type: audio/wav
```

- 对象名前缀 `OpenETS-` ⚠️
- 头部常量确证：`x-oss-security-token`、`x-oss-object-acl`、`success_action_status`、`content-length-range`、`multipart/form-data; boundary=`
- 上传完成后把 OSS 对象 URL 作为录音引用回填到提交流程
- 上传器还引用 `CryptoKit.SymmetricKey` —— 用于生成随机 boundary/键 ⚠️

---

## 8. 语音评测（WebSocket + 私有 protobuf + XML 评分）

这是整套协议里最“重”的部分，完全自研：

### 8.1 两步鉴权 ✅

```text
① POST https://api.ets100.com/ai/aiplat/getaitoken     → ai token
② WS   wss://oe-pub.eduaiplat.com/ws/v2/ise
   header: authorization, x-device, x-user-id, x-trace-id
```

（握手头由内联字符串还原：`authoriz…`、`x-device`、`x-user-id`、`x-trace-…`，常量值 `OpenETS-x-device`）

### 8.2 帧格式 ✅

`ETSSpeechWire.decode` 实现了 **protobuf 子集**（varint / fixed32 / fixed64 / length-delimited），错误文案：`speech protobuf field / wire type / length / integer / fixed value`。

发送内容（字段名反汇编所得）：

```text
engine_sdk_config, engine_config_list, default_engine_sdk, global_default_rule
scene_id, scene_type, plat_type, plat_ver
homework…, entity_id, category, rate_scale
audio_store…, upload_f…, detail_f…, xml_resu…, xml_scor…
```

音频：`readPCM` 读取 PCM（16 kHz / 单声道 / 16-bit ⚠️），分片写入。

### 8.3 评分解析 ✅

响应是 **XML**（`ETSSpeechXML`，`<!DOCTYPE…>`），提取：

```text
total_score, real_score, rate_scale, category, is_reject, exception…
```

- 类别：`read_word` / `read_sentence` / `read_char` / `rec_paper` / `free_choice` / `free_expr` / `simple_expression`
- 特殊容错：`ignore_28707_error`（引擎错误码 28707 直接忽略）
- 阈值：`pc_accept_reject_score`（拒绝分界）

---

## 9. 客户端自带“调试后门”（值得注意）

`OpenETS/Tools/` 里有一整套 API 调试工具，说明作者自己也在联调：

- `ETSAPIDiagnostics` —— 内存记录每次调用：`ETSAPICall{accountID, route, timestamp, status, elapsed, request, response}`
- `APIActivityView` —— 当前会话请求流水（`No exam API requests have been sent in this app session.`）
- `APIDashboardView` —— 可选择一条作业、生成“测试答卷”并**直接提交到 ETS**
- `APITestDraft` —— 生成假答案/假用时，`Send test result to ETS`、`The server confirmed the submitted test result.`

即：**该 App 本身就是“半成品答题机器人 + 协议验证器”**，其提交链路可直接用于压测/验证接口。

---

## 10. 安全与合规评估

| 项 | 结论 |
|---|---|
| 传输 | 全程 HTTPS/WSS，无 ATS 例外，未见证书固定（无 pinning 代码） ✅ |
| 签名 | **MD5**，密钥外部注入；MD5 可碰撞但不影响这里的“密钥前缀”用法 ⚠️ |
| 凭据 | 密码仅做 Base64+XOR 级混淆后上送 ❗真正的保护全靠 TLS |
| 本地存储 | Keychain 存 token 与账号密码（`saveCredentials`） |
| 敏感数据 | `ETSAPIDiagnostics` 把完整请求/响应（含 token、录音 URL）留在内存，无脱敏 |
| 分包/越权 | 无越权接口迹象；写操作仅 4 个考试路由 |
| 证书 | 个人团队签名、单设备、`get-task-allow`，**2026-10-22 到期** |

---

## 11. 对自建客户端的可行路径（务实建议）

1. **必须解决的阻塞点：`requestSecretBase64` 与 `pid`**
   二进制里没有内置，需从以下途径之一获得：原官方客户端的协议配置、抓包样本反推（有 sign 与 body 时可离线穷举/校验）、或作者放出的兼容性配置。
   拿到后即可完全复现 `sign`。

2. **最小可用链路**：

```text
user/login → 拿 token → g/homework/list → m/homework/struct（取卷）
          → g/homework/checkstart（校验）→ m/homework/set-use-time
          → [朗读题] getaitoken + wss 评测 + STS 上传
          → m/homework/submit（分 answer / finalization 两阶段）
          → g/homework/score-detail（核对结果）
```

3. **时序/幂等要点**（代码里明确存在）：
   - `head.time` 与 `time_second` 必须同一时刻、且时间格式严格 `yyyy-MM-dd HH:mm:ss`
   - 提交状态有 `uncertain` 分支：网络失败时**不能盲目重发**，需先 `score-detail`/`checkstart` 核对
   - 逾期/次数限制在客户端和服务端双重校验

4. **合规风险**：这是第三方重实现，登录环节已明确提示“设备校验/账号确认需在原客户端完成”，考试场景下替考/自动化提交违反 ETS 条款，且个人签名 24 小时内到期。

---

## 附录 A：分析方法与产物位置

| 产物 | 路径 |
|---|---|
| 解包内容 | `temp\xcarchive_extract\`（含 .app 与 dSYM） |
| 全量字符串 | `temp\strings_all.txt`（9,464 条）、`temp\strings_dsym.txt`（63,612 条） |
| 结构体字段（Swift 反射） | `temp\swift_fields.txt`（100 个类型的完整字段表） |
| 分析脚本 | `temp\macho.py` `dwarf.py` `swiftmeta.py` `disasm.py` `ann.py` `callgraph.py` `refs.py` |

关键技术手段：

1. **Mach-O 解析**：`__stubs` + `__got` + indirect symbol table → 881 个外部调用全部映射到符号名
2. **DWARF v5**：`__debug_info`（1.6 MB）解析出 12 万 DIE，用 `DW_AT_specification` 回溯拿到 3,266 个函数的 PC 区间
3. **Swift 反射元数据**：`__swift5_types` → 类型描述符 → `FieldDescriptor` → 字段名与类型（这是拿到 `k_serves / requestSecretBase64` 的关键）
4. **ARM64 子集反汇编器**（自写）：识别 `movz/movk` 立即数串，还原 **Swift small-string**（≤15 字节常量不落 `__cstring`，常规 `strings` 抓不到 —— `g/homework/list`、`m/class/list`、`system/config`、`m/ecard/list`、`g/set/ppr-res`、`m/audio/sync-v2`、`user/login` 全是这样挖出来的）
5. **全局字符串交叉引用**：4,406 个字面量引用点 → 归属函数，用于确认端点由哪个客户端类发出

## 附录 B：尚未确证的点（建议抓包验证）

1. `sign` 三段拼接顺序（secret / body / timestamp）与十六进制大小写
2. `head.version` 取值：`profile.clientVersion` 还是 `session.clientVersion`
3. 语音评测 PCM 的具体参数（采样率/位深/声道）与分包节奏
4. `ETSProtocolProfile` 的实际注入值（运行时由配置/设置页提供）
5. `encryptCredential` 的确切变换（XOR key 的取法）
