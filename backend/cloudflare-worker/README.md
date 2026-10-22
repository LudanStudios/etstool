# ETS 免费听力代理（Cloudflare Worker）

给没有配置 DeepSeek Key 的用户提供每日免费额度。Key 只保存在 Worker Secret 里，客户端永远拿不到。

## 限制

- 每设备每天 **10 次**
- 每 IP 每天 **30 次**（防止随便换设备 ID）
- 每设备每分钟 **5 次**
- 一次请求 = 一整段/整张听力（`sections` 里所有子题），不是每道子题各算一次
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

# 5. 部署
npx wrangler deploy
```

部署后会得到类似：

```
https://ets-free-listening.<account>.workers.dev
```

先测健康检查：

```bash
curl https://ets-free-listening.<account>.workers.dev/health
```

返回：

```json
{"ok":true,"configured":true,"limits":{"deviceDaily":10,"ipDaily":30}}
```

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
- 每段 1-20 题，每题 2-6 个选项
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
- `invalid_payload`
- `rate_limited`
- `ip_quota_exceeded`
- `upstream_error`
- `server_not_configured`

## 客户端需要配合的事

1. 生成稳定设备 ID：
   - `SHA-256(MachineGuid + 系统盘序列号 + 固定 salt)`
   - 不要每次启动随机生成
2. 每个请求生成新的 `request_id`；网络重试复用同一个
3. 请求体里放听力原文（`st_nr` / transcript）和所有子题
4. 收到 429 时显示“免费额度已用完，去填自己的 Key”
5. 收到 `remaining_today` 更新界面
6. 客户端**不保存** DeepSeek Key，只保存自己的 Key（如果用户选择填）

## 安全注意

- `DEEPSEEK_API_KEY` 只放 Secret，不要写进 `wrangler.toml` 或代码
- 两个 salt 要随机长字符串；换了 salt 后所有历史设备/IP 哈希都会变
- 原始 IP/设备 ID 不落库，只存哈希
- 定期清理 `requests` 表（例如 30 天前），否则会一直增长
- Cloudflare 国内访问可能不稳定；如果主要用户在国内，后续可以换阿里云函数计算，接口契约不变

## 清理旧请求（可选）

在 Cloudflare Dashboard 建一个 Cron Trigger，定期执行：

```sql
DELETE FROM requests WHERE created_at < datetime('now', '-30 days');
```

或者在 Worker 里加一个受保护的定时任务。
