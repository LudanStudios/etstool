# ETS 免费听力代理（Cloudflare Worker）

给没有配置本机凭据的用户提供听力、口语生成、试卷解析和 TTS 每日体验额度。DeepSeek Key 与阿里云 AccessKey/AppKey 只保存在 Worker Secret 里，客户端永远拿不到。

## 限制

- 每设备每天 **10 次完整听力请求**
- 每 IP 每天 **30 次**（防止随便换设备 ID）
- 每设备每分钟 **5 次**
- 一次请求会把当前试卷中缺少本地标答的听力段及其子题一起提交，不按子题扣次数
- 口语生成和阿里云 TTS 也分别为每设备每天 **10 次**、每 IP 每天 **30 次**；每次口语答案生成或完整回答合成计 1 次，TTS 的 300 字符分段不额外扣次数
- 试卷详细解析每设备每天 **10 次**、每 IP 每天 **30 次**；每次完整试卷解析计 1 次，Worker 内部将题目拆成多组调用 DeepSeek 不会重复扣额度
- 额度按北京时间 00:00 重置

设备 ID 和 IP 只能防普通滥用，不能防伪造；不要把它当安全边界。

## 部署

```bash
cd backend/cloudflare-worker
npm install
npx wrangler login

# 1. 创建 D1
npx wrangler d1 create ets_free_listening

# 2. 把输出的 database_id 填到 wrangler.toml
#    [[d1_databases]] database_id = "..."

# 3. 建表
npx wrangler d1 execute ets_free_listening --file=./schema.sql --remote

# 4. 配置密钥
npx wrangler secret put DEEPSEEK_API_KEY
npx wrangler secret put DEVICE_HASH_SALT
npx wrangler secret put IP_HASH_SALT
npx wrangler secret put ALIYUN_ACCESS_KEY_ID
npx wrangler secret put ALIYUN_ACCESS_KEY_SECRET
npx wrangler secret put ALIYUN_APP_KEY

# 5. 部署
npx wrangler deploy
```

部署后会得到类似：

```
https://ets-free-listening.<account>.workers.dev
```

ETS 助手内置的 Worker 根地址为 `https://ets.azursmelt.us.ci`，用户无需填写。没有本机 DeepSeek Key 时，口语生成、试卷详细解析和免密听力使用 Worker；没有本机阿里云凭据时，TTS 使用 Worker。配置本机凭据后会直连服务商，不使用 Worker 额度。各服务独立计数；口语生成和试卷解析成功响应按 request ID 缓存，TTS 音频不缓存，相同 ID 的重复合成会提示客户端使用新 ID。

客户端先在本地对 Windows MachineGuid 做 SHA-256，再发送设备哈希；Worker 再用 Secret 盐值哈希设备标识和 Cloudflare 提供的来源 IP，D1 只保存哈希值。设备 ID 和 IP 主要用于限制普通滥用，不是强身份认证。

先测健康检查：

```bash
curl https://ets-free-listening.<account>.workers.dev/health
```

返回：

```json
{"ok":true,"configured":true,"services":{"listening":true,"speaking":true,"analysis":true,"tts":true},"limits":{"deviceDaily":10,"ipDaily":30,"resetTimezone":"Asia/Shanghai"}}
```

体验模式偏好设置会调用 `GET /v1/usage` 并发送 `X-Device-Id` 请求头。接口只返回当前设备和 Cloudflare 来源 IP 的听力、口语、试卷解析、TTS 已用次数、剩余次数与北京时间重置时间；不返回原始 IP 或哈希值，并使用 `Cache-Control: no-store`。此查询不消耗额度。

## 本地开发

```bash
cp .dev.vars.example .dev.vars
# 填入自己的 Key 和两个随机 salt
npm install
npm run db:init:local
npm run dev
```

`wrangler dev` 默认在 `http://127.0.0.1:8787`。

## API

### `POST /v1/speaking/answer`

请求体为 `{ "request_id": "...", "stem": "口语题干", "instructions": "llm_stt.md 内容" }`。成功返回 `text`、`remaining_today` 和 `reset_at`。

### `GET /v1/usage`

需要 `X-Device-Id` 请求头。返回 `services.listening`、`services.speaking`、`services.analysis`、`services.tts` 下的 `device` 与 `ip` 用量计数器（`used`、`limit`、`remaining`），以及 `checked_at` 和 `reset_at`。查询本身不扣额度。

### `POST /v1/paper-analysis`

请求体为 `{ "request_id": "...", "title": "试卷名称", "batches": ["第 1 组题目…", "第 2 组题目…"] }`。最多 12 组、每组最多 32000 字符、总内容最多 160000 字符且请求体最多 600 KB。成功返回整卷 Markdown、`remaining_today` 和 `reset_at`。每次通过校验的完整解析请求立即扣 1 次 `analysis` 额度；Worker 内部逐组请求 DeepSeek，不会重复扣额。DeepSeek 上游失败也不退还本次额度，错误响应会说明已扣次数与剩余设备额度。相同 request ID 的成功或失败响应均可幂等重试，不会再次扣额度。

### `POST /v1/tts/synthesize`

请求体为 `{ "request_id": "...", "texts": ["最多 300 字符的一段", "下一段"] }`。Worker 获取 token 并依次合成分段 WAV，以 `ETSW` 二进制帧返回；剩余额度在 `x-remaining-today` 响应头。最多 20 段，每段 1-300 字符。

### `POST /v1/listening/solve`

请求头：

```
Content-Type: application/json
X-Device-Id: <稳定的设备哈希，16-160 字符>
```

请求体：

```json
{
  "request_id": "uuid-or-random-string",
  "sections": [
    {
      "passage": "听力原文或字幕文本……",
      "questions": [
        {
          "number": 1,
          "prompt": "What does the man want to do?",
          "options": ["A. ...", "B. ...", "C. ...", "D. ..."]
        }
      ]
    }
  ]
}
```

说明：

- `sections` 1-10 段，总题数最多 60
- 每段必须有 `passage`，20-12000 字符
- 每段 1-20 题，每题 2-4 个选项
- `request_id` 用于幂等：同一个 ID 重试不会重复扣额度

成功响应：

```json
{
  "answers": [
    {"section": 1, "number": 1, "answer": "A"},
    {"section": 1, "number": 2, "answer": "C"}
  ],
  "remaining_today": 9,
  "reset_at": "2026-10-23T16:00:00.000Z"
}
```

额度用完：

```json
{
  "error": "daily_quota_exceeded",
  "message": "今日免费额度已用完，请填写自己的 API Key 或明天再试。",
  "remaining_today": 0,
  "reset_at": "2026-10-23T16:00:00.000Z"
}
```

HTTP 429。

其他错误码：

- `invalid_device_id`
- `invalid_request_id`
- `request_id_conflict`
- `invalid_payload`
- `rate_limited`
- `ip_quota_exceeded`
- `upstream_error`
- `server_not_configured`

## 客户端需要配合的事

1. 保持设备 ID 稳定；当前客户端使用 Windows MachineGuid 的本地 SHA-256 哈希
2. 每个完整听力请求使用新的 `request_id`；网络重试复用同一个
3. 请求体包含听力原文（`st_nr` / `xt_value`）、子题和选项
4. 收到 429 时提示用户检查今日额度，或填写自己的 DeepSeek Key
5. 客户端**不保存** Worker 使用的 DeepSeek Key

## 安全注意

- `DEEPSEEK_API_KEY`、`ALIYUN_ACCESS_KEY_ID`、`ALIYUN_ACCESS_KEY_SECRET`、`ALIYUN_APP_KEY` 只放 Secret，不要写进 `wrangler.toml` 或代码
- 两个 salt 要随机长字符串；换了 salt 后所有历史设备/IP 哈希都会变
- 原始 IP/设备 ID 不落库，只存哈希
- Worker 每日 Cron 会自动清理 30 天前的幂等记录和 40 天前的额度记录
- Cloudflare 国内访问可能不稳定；如果主要用户在国内，后续可以换阿里云函数计算，接口契约不变

