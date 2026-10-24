/**
 * ETS free listening proxy on Cloudflare Workers + D1.
 *
 * The DeepSeek key lives only in Worker secrets. The client sends a stable
 * device id; the Worker enforces daily per-device and per-IP quotas and keeps
 * an idempotency record so retries do not consume another free request.
 */
const DEVICE_DAILY_LIMIT = 10;
const IP_DAILY_LIMIT = 30;
const DEVICE_MINUTE_LIMIT = 5;
const MAX_BODY_BYTES = 100_000;
const DEEPSEEK_ENDPOINT = "https://api.deepseek.com/chat/completions";
const ALIYUN_META_ENDPOINT = "https://nls-meta.cn-shanghai.aliyuncs.com/";
const ALIYUN_TTS_ENDPOINT = "https://nls-gateway-cn-shanghai.aliyuncs.com/stream/v1/tts";
const MAX_TTS_CHUNKS = 20;
let aliyunTokenCache = null;

export default {
  async scheduled(controller, env, ctx) {
    const requestCutoff = new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString();
    const usageCutoff = new Date(Date.now() - 40 * 24 * 60 * 60 * 1000 + 8 * 60 * 60 * 1000)
      .toISOString().slice(0, 10);
    ctx.waitUntil(Promise.all([
      env.DB.prepare("DELETE FROM requests WHERE created_at < ?").bind(requestCutoff).run(),
      env.DB.prepare("DELETE FROM daily_usage WHERE day < ?").bind(usageCutoff).run()
    ]));
  },

  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === "OPTIONS") return withCors(new Response(null, { status: 204 }));

    if (url.pathname === "/health") {
      const common = Boolean(env.DEVICE_HASH_SALT && env.IP_HASH_SALT && env.DB);
      const listening = Boolean(common && env.DEEPSEEK_API_KEY);
      return json({
        ok: true,
        configured: listening,
        services: {
          listening,
          speaking: Boolean(listening),
          analysis: Boolean(listening),
          tts: Boolean(common && env.ALIYUN_ACCESS_KEY_ID && env.ALIYUN_ACCESS_KEY_SECRET && env.ALIYUN_APP_KEY)
        },
        limits: { deviceDaily: DEVICE_DAILY_LIMIT, ipDaily: IP_DAILY_LIMIT, resetTimezone: "Asia/Shanghai" }
      });
    }

    if (url.pathname === "/v1/usage")
      return handleUsage(request, env);

    if (url.pathname === "/v1/speaking/answer")
      return handleSpeakingAnswer(request, env);
    if (url.pathname === "/v1/paper-analysis")
      return handlePaperAnalysis(request, env);
    if (url.pathname === "/v1/tts/synthesize")
      return handleTtsSynthesize(request, env);

    if (url.pathname !== "/v1/listening/solve") return error(404, "not_found", "接口不存在。");
    if (request.method !== "POST") return error(405, "method_not_allowed", "只支持 POST。");
    if (!env.DEEPSEEK_API_KEY) return error(500, "server_not_configured", "服务端尚未配置 DeepSeek Key。");
    if (!env.DEVICE_HASH_SALT || !env.IP_HASH_SALT) {
      return error(500, "server_not_configured", "服务端尚未配置设备/IP 哈希盐值。");
    }
    if (!env.DB) return error(500, "server_not_configured", "服务端尚未绑定 D1 数据库。");

    const deviceId = (request.headers.get("X-Device-Id") || "").trim();
    if (!/^[A-Za-z0-9._:-]{16,160}$/.test(deviceId)) {
      return error(400, "invalid_device_id", "缺少或无效的 X-Device-Id（至少 16 个字符）。");
    }

    const contentLength = Number(request.headers.get("content-length") || 0);
    if (contentLength > MAX_BODY_BYTES) return error(413, "body_too_large", "请求体过大。");

    let body;
    try {
      body = JSON.parse(await readBodyLimited(request, MAX_BODY_BYTES));
    } catch (err) {
      if (err?.code === "body_too_large") return error(413, "body_too_large", "请求体过大。");
      return error(400, "invalid_json", "请求体必须是 JSON。");
    }

    const parsed = validatePayload(body);
    if (parsed.error) return error(400, "invalid_payload", parsed.error);

    const requestId = String(body.request_id || "").trim();
    if (!/^[A-Za-z0-9._:-]{8,120}$/.test(requestId)) {
      return error(400, "invalid_request_id", "request_id 必填，且需为 8-120 个字母数字/._:-。");
    }

    const ip = request.headers.get("CF-Connecting-IP") || "0.0.0.0";
    const deviceHash = await sha256(`${env.DEVICE_HASH_SALT || "device"}:${deviceId}`);
    const ipHash = await sha256(`${env.IP_HASH_SALT || "ip"}:${ip}`);
    const { day, resetAt } = shanghaiDay();

    // Idempotency: a repeated request_id must not consume another free request.
    try {
      const existing = await env.DB.prepare(
        "SELECT device_hash, status, response_json, created_at FROM requests WHERE request_id = ?"
      ).bind(requestId).first();
      if (existing) {
        if (existing.device_hash !== deviceHash) {
          return error(409, "request_id_conflict", "request_id 已由其他设备使用，请重新请求。");
        }
        if (existing.status === "completed" && existing.response_json) {
          return withCors(new Response(existing.response_json, {
            status: 200,
            headers: { "content-type": "application/json; charset=utf-8" }
          }));
        }
        const age = Date.now() - Date.parse(existing.created_at || 0);
        if (existing.status === "pending" && Number.isFinite(age) && age > 10 * 60_000) {
          await deleteRequest(env, requestId);
        } else {
          return error(409, "request_in_progress", "相同的 request_id 正在处理中，请稍后重试。");
        }
      }
    } catch (err) {
      return error(500, "database_error", `读取幂等记录失败：${err.message}`);
    }

    const minuteAgo = new Date(Date.now() - 60_000).toISOString();
    try {
      const minute = await env.DB.prepare(
        "SELECT COUNT(*) AS count FROM requests WHERE device_hash = ? AND created_at > ?"
      ).bind(deviceHash, minuteAgo).first();
      if ((minute?.count || 0) >= DEVICE_MINUTE_LIMIT) {
        return quotaError("rate_limited", "请求过于频繁，请稍后再试。", 0, resetAt);
      }
    } catch (err) {
      return error(500, "database_error", `读取频率限制失败：${err.message}`);
    }

    try {
      await env.DB.prepare(
        "INSERT INTO requests(request_id, device_hash, ip_hash, status, created_at) VALUES(?,?,?,'pending',?)"
      ).bind(requestId, deviceHash, ipHash, new Date().toISOString()).run();
    } catch {
      return error(409, "request_in_progress", "相同的 request_id 正在处理中，请稍后重试。");
    }

    let deviceCount;
    try {
      deviceCount = await reserve(env, day, "device", deviceHash, DEVICE_DAILY_LIMIT);
    } catch (err) {
      await deleteRequest(env, requestId);
      return error(500, "database_error", `预留设备额度失败：${err.message}`);
    }
    if (deviceCount === null) {
      await deleteRequest(env, requestId);
      return quotaError("daily_quota_exceeded", "今日免费额度已用完，请填写自己的 API Key 或明天再试。", 0, resetAt);
    }

    let ipCount;
    try {
      ipCount = await reserve(env, day, "ip", ipHash, IP_DAILY_LIMIT);
    } catch (err) {
      await release(env, day, "device", deviceHash);
      await deleteRequest(env, requestId);
      return error(500, "database_error", `预留 IP 额度失败：${err.message}`);
    }
    if (ipCount === null) {
      await release(env, day, "device", deviceHash);
      await deleteRequest(env, requestId);
      return quotaError("ip_quota_exceeded", "当前网络今日免费额度已用完，请明天再试或填写自己的 API Key。", 0, resetAt);
    }

    const remaining = Math.max(0, DEVICE_DAILY_LIMIT - deviceCount);

    try {
      const answers = await solveWithDeepSeek(env, parsed.sections);
      const payload = JSON.stringify({
        answers,
        remaining_today: remaining,
        reset_at: resetAt
      });
      await env.DB.prepare(
        "UPDATE requests SET status = 'completed', response_json = ? WHERE request_id = ?"
      ).bind(payload, requestId).run();
      return withCors(new Response(payload, {
        status: 200,
        headers: { "content-type": "application/json; charset=utf-8" }
      }));
    } catch (err) {
      await release(env, day, "device", deviceHash);
      await release(env, day, "ip", ipHash);
      await deleteRequest(env, requestId);
      return error(502, "upstream_error", `生成失败：${err.message}`);
    }
  }
};

async function handleUsage(request, env) {
  if (request.method !== "GET") return error(405, "method_not_allowed", "只支持 GET。");
  if (!hasCommonConfiguration(env))
    return error(500, "server_not_configured", "服务端数据库或哈希盐尚未配置。");

  const deviceId = (request.headers.get("X-Device-Id") || "").trim();
  if (!/^[A-Za-z0-9._:-]{16,160}$/.test(deviceId))
    return error(400, "invalid_device_id", "缺少或无效的 X-Device-Id（至少 16 个字符）。");

  const ip = request.headers.get("CF-Connecting-IP") || "0.0.0.0";
  const deviceHash = await sha256(`${env.DEVICE_HASH_SALT}:${deviceId}`);
  const ipHash = await sha256(`${env.IP_HASH_SALT}:${ip}`);
  const { day, resetAt } = shanghaiDay();
  try {
    const result = await env.DB.prepare(
      `SELECT scope, key, count FROM daily_usage WHERE day = ? AND (
        (scope = 'device' AND key = ?) OR (scope = 'ip' AND key = ?) OR
        (scope = 'speaking:device' AND key = ?) OR (scope = 'speaking:ip' AND key = ?) OR
        (scope = 'analysis:device' AND key = ?) OR (scope = 'analysis:ip' AND key = ?) OR
        (scope = 'tts:device' AND key = ?) OR (scope = 'tts:ip' AND key = ?)
      )`
    ).bind(day, deviceHash, ipHash, deviceHash, ipHash, deviceHash, ipHash, deviceHash, ipHash).all();
    const counts = new Map((result.results || []).map(row => [`${row.scope}:${row.key}`, Number(row.count) || 0]));
    const serviceUsage = prefix => {
      const deviceScope = prefix ? `${prefix}:device` : "device";
      const ipScope = prefix ? `${prefix}:ip` : "ip";
      const deviceUsed = counts.get(`${deviceScope}:${deviceHash}`) || 0;
      const ipUsed = counts.get(`${ipScope}:${ipHash}`) || 0;
      return {
        device: { used: deviceUsed, limit: DEVICE_DAILY_LIMIT, remaining: Math.max(0, DEVICE_DAILY_LIMIT - deviceUsed) },
        ip: { used: ipUsed, limit: IP_DAILY_LIMIT, remaining: Math.max(0, IP_DAILY_LIMIT - ipUsed) }
      };
    };
    return withCors(new Response(JSON.stringify({
      services: {
        listening: serviceUsage(""),
        speaking: serviceUsage("speaking"),
        analysis: serviceUsage("analysis"),
        tts: serviceUsage("tts")
      },
      checked_at: new Date().toISOString(),
      reset_at: resetAt
    }), {
      status: 200,
      headers: {
        "content-type": "application/json; charset=utf-8",
        "cache-control": "private, no-store, max-age=0"
      }
    }));
  } catch {
    return error(500, "database_error", "读取今日用量失败，请稍后重试。");
  }
}

async function handleSpeakingAnswer(request, env) {
  if (request.method !== "POST") return error(405, "method_not_allowed", "只支持 POST。");
  if (!hasCommonConfiguration(env)) return error(500, "server_not_configured", "服务端数据库或哈希盐尚未配置。");
  if (!env.DEEPSEEK_API_KEY) return error(500, "server_not_configured", "服务端尚未配置 DeepSeek Key。");
  const parsed = await readServiceBody(request, 80_000);
  if (parsed.error) return parsed.error;
  const { body } = parsed;
  const stem = String(body.stem || "").trim();
  const instructions = String(body.instructions || "").trim();
  if (stem.length < 1 || stem.length > 4_000) return error(400, "invalid_payload", "口语题干长度需为 1-4000 字符。");
  if (instructions.length < 1 || instructions.length > 70_000)
    return error(400, "invalid_payload", "口语提示词长度需为 1-70000 字符。");

  const started = await beginQuotaRequest(request, env, body.request_id, "speaking");
  if (started.response) return started.response;
  const state = started.state;
  try {
    const response = await fetch(DEEPSEEK_ENDPOINT, {
      method: "POST",
      headers: { "Authorization": `Bearer ${env.DEEPSEEK_API_KEY}`, "Content-Type": "application/json" },
      body: JSON.stringify({
        model: "deepseek-flash",
        messages: [
          { role: "system", content: instructions },
          { role: "user", content: stem }
        ],
        stream: false,
        temperature: 0.7
      })
    });
    const raw = await response.text();
    if (!response.ok) throw new Error(`DeepSeek HTTP ${response.status}: ${extractUpstreamMessage(raw)}`);
    let text;
    try { text = String(JSON.parse(raw)?.choices?.[0]?.message?.content || "").trim(); }
    catch { throw new Error("DeepSeek 返回了非 JSON 响应。"); }
    if (!text) throw new Error("DeepSeek 返回了空文本。");
    const payload = JSON.stringify({ text, remaining_today: state.remaining, reset_at: state.resetAt });
    await completeQuotaRequest(env, state, payload);
    return jsonText(payload);
  } catch (err) {
    await rollbackQuotaRequest(env, state);
    return error(502, "upstream_error", `口语生成失败：${err.message}`);
  }
}

async function handlePaperAnalysis(request, env) {
  if (request.method !== "POST") return error(405, "method_not_allowed", "只支持 POST。");
  if (!hasCommonConfiguration(env)) return error(500, "server_not_configured", "服务端数据库或哈希盐尚未配置。");
  if (!env.DEEPSEEK_API_KEY) return error(500, "server_not_configured", "服务端尚未配置 DeepSeek Key。");

  const parsed = await readServiceBody(request, 600_000);
  if (parsed.error) return parsed.error;
  const { body } = parsed;
  const title = String(body.title || "").trim();
  const batches = body.batches;
  if (title.length < 1 || title.length > 200)
    return error(400, "invalid_payload", "试卷标题长度需为 1-200 字符。");
  if (!Array.isArray(batches) || batches.length < 1 || batches.length > 12 ||
      batches.some(batch => typeof batch !== "string" || batch.trim().length < 1 || batch.trim().length > 32_000))
    return error(400, "invalid_payload", "试卷解析需要 1-12 组题目，每组不得超过 32000 字符。");
  const normalizedBatches = batches.map(batch => batch.trim());
  if (normalizedBatches.reduce((total, batch) => total + batch.length, 0) > 160_000)
    return error(400, "invalid_payload", "试卷内容超过单次体验模式解析上限。");

  // One quota reservation covers the entire paper. Internal DeepSeek batch calls
  // do not reserve additional analysis quota.
  const started = await beginQuotaRequest(request, env, body.request_id, "analysis");
  if (started.response) return started.response;
  const state = started.state;
  try {
    const sections = [`# ${title}：逐题解析`, ""];
    for (let index = 0; index < normalizedBatches.length; index++) {
      const answer = await askPaperAnalysisBatch(env, title, normalizedBatches[index]);
      if (index > 0) sections.push("", "---", "");
      sections.push(answer);
    }
    const payload = JSON.stringify({
      markdown: sections.join("\n").trim(),
      remaining_today: state.remaining,
      reset_at: state.resetAt
    });
    await completeQuotaRequest(env, state, payload);
    return jsonText(payload);
  } catch (err) {
    const message = `试卷解析失败；本次有效请求已扣 1 次试卷解析额度，不予退还：${err.message}`;
    const payload = JSON.stringify({
      error: "upstream_error",
      message,
      remaining_today: state.remaining,
      reset_at: state.resetAt
    });
    // Analysis is charged when an accepted request reserves quota. A provider
    // failure does not refund the user's one-request allowance.
    try {
      await env.DB.prepare(
        "UPDATE requests SET status = 'failed', response_json = ? WHERE request_id = ?"
      ).bind(payload, state.requestId).run();
    } catch {
      // Keep the quota reservation even if recording the failure is unavailable.
    }
    return withCors(new Response(payload, {
      status: 502,
      headers: { "content-type": "application/json; charset=utf-8" }
    }));
  }
}

async function askPaperAnalysisBatch(env, title, questions) {
  const system = [
    "你是英语考试讲解老师。请按题号逐题解析：先还原题意，再指出考查点，逐项排除干扰项，说明正确答案依据，并补充必要的词汇或语法。",
    "听力题如果没有音频转写，不要臆造听力原文，说明仅基于题面和答案解析。",
    "输出适合打印的中文 Markdown，内容准确、简明。使用清晰的标题层级；答案速查优先使用表格；逐题解析使用小标题、加粗标签和列表，避免输出原始字段堆叠。",
    questions.includes('"口语题干"')
      ? "对于包含口语题干的题目，结合题干说明作答要求；没有提供口语题干的题目不要自行补写。"
      : ""
  ].filter(Boolean).join(" ");
  const fields = questions.includes('"口语题干"')
    ? "以下是试题字段、口语题干与标准答案："
    : "以下是试题字段与标准答案：";
  const response = await fetch(DEEPSEEK_ENDPOINT, {
    method: "POST",
    headers: {
      "Authorization": `Bearer ${env.DEEPSEEK_API_KEY}`,
      "Content-Type": "application/json"
    },
    body: JSON.stringify({
      model: "deepseek-flash",
      messages: [
        { role: "system", content: system },
        { role: "user", content: `试卷：${title}\n${fields}\n${questions}` }
      ],
      stream: false,
      temperature: 0.3,
      max_tokens: 4096
    })
  });
  const raw = await response.text();
  if (!response.ok) throw new Error(`DeepSeek HTTP ${response.status}: ${extractUpstreamMessage(raw)}`);
  let text;
  try { text = String(JSON.parse(raw)?.choices?.[0]?.message?.content || "").trim(); }
  catch { throw new Error("DeepSeek 返回了非 JSON 响应。"); }
  if (!text) throw new Error("DeepSeek 返回了空解析内容。");
  return text;
}

async function handleTtsSynthesize(request, env) {
  if (request.method !== "POST") return error(405, "method_not_allowed", "只支持 POST。");
  if (!hasCommonConfiguration(env)) return error(500, "server_not_configured", "服务端数据库或哈希盐尚未配置。");
  if (!env.ALIYUN_ACCESS_KEY_ID || !env.ALIYUN_ACCESS_KEY_SECRET || !env.ALIYUN_APP_KEY)
    return error(500, "server_not_configured", "服务端尚未配置阿里云 TTS 凭据。");
  const parsed = await readServiceBody(request, MAX_BODY_BYTES);
  if (parsed.error) return parsed.error;
  const { body } = parsed;
  const chunks = body.texts;
  if (!Array.isArray(chunks) || chunks.length < 1 || chunks.length > MAX_TTS_CHUNKS ||
      chunks.some(value => typeof value !== "string" || value.trim().length < 1 || value.trim().length > 300))
    return error(400, "invalid_payload", `texts 必须包含 1-${MAX_TTS_CHUNKS} 段，每段为 1-300 个字符。`);

  const started = await beginQuotaRequest(request, env, body.request_id, "tts");
  if (started.response) return started.response;
  const state = started.state;
  try {
    const token = await getAliyunToken(env);
    const audioChunks = [];
    for (const text of chunks) {
      const audio = await synthesizeAliyunChunk(env.ALIYUN_APP_KEY, token, text.trim());
      audioChunks.push(audio);
    }
    const payload = encodeAudioChunks(audioChunks);
    await completeQuotaRequest(env, state, null);
    return withCors(new Response(payload, { status: 200, headers: {
      "content-type": "application/octet-stream",
      "x-remaining-today": String(state.remaining)
    } }));
  } catch (err) {
    await rollbackQuotaRequest(env, state);
    return error(502, "upstream_error", `阿里云 TTS 失败：${err.message}`);
  }
}

function hasCommonConfiguration(env) {
  return Boolean(env.DB && env.DEVICE_HASH_SALT && env.IP_HASH_SALT);
}

async function readServiceBody(request, maxBytes) {
  const contentLength = Number(request.headers.get("content-length") || 0);
  if (contentLength > maxBytes) return { error: error(413, "body_too_large", "请求体过大。") };
  try {
    const body = JSON.parse(await readBodyLimited(request, maxBytes));
    if (!body || typeof body !== "object" || Array.isArray(body))
      return { error: error(400, "invalid_json", "请求体必须是 JSON 对象。") };
    return { body };
  } catch (err) {
    if (err?.code === "body_too_large") return { error: error(413, "body_too_large", "请求体过大。") };
    return { error: error(400, "invalid_json", "请求体必须是 JSON。") };
  }
}

async function beginQuotaRequest(request, env, rawRequestId, service) {
  const requestIdValue = String(rawRequestId || "").trim();
  if (!/^[A-Za-z0-9._:-]{8,120}$/.test(requestIdValue))
    return { response: error(400, "invalid_request_id", "request_id 必填，且需为 8-120 个字母数字/._:-。") };
  const deviceId = (request.headers.get("X-Device-Id") || "").trim();
  if (!/^[A-Za-z0-9._:-]{16,160}$/.test(deviceId))
    return { response: error(400, "invalid_device_id", "缺少或无效的 X-Device-Id（至少 16 个字符）。") };

  const requestId = `${service}:${requestIdValue}`;
  const ip = request.headers.get("CF-Connecting-IP") || "0.0.0.0";
  const deviceHash = await sha256(`${env.DEVICE_HASH_SALT}:${deviceId}`);
  const ipHash = await sha256(`${env.IP_HASH_SALT}:${ip}`);
  const { day, resetAt } = shanghaiDay();
  let inserted = false;
  let deviceReserved = false;
  try {
    const existing = await env.DB.prepare(
      "SELECT device_hash, status, response_json, created_at FROM requests WHERE request_id = ?"
    ).bind(requestId).first();
    if (existing) {
      if (existing.device_hash !== deviceHash)
        return { response: error(409, "request_id_conflict", "request_id 已由其他设备使用，请重新请求。") };
      if (existing.status === "completed" && existing.response_json)
        return { response: withCors(new Response(existing.response_json, { status: 200,
          headers: { "content-type": "application/json; charset=utf-8" } })) };
      if (existing.status === "completed")
        return { response: error(409, "request_already_completed", "该请求已完成；请使用新的 request_id 发起新合成。") };
      if (existing.status === "failed" && existing.response_json)
        return { response: withCors(new Response(existing.response_json, { status: 502,
          headers: { "content-type": "application/json; charset=utf-8" } })) };
      if (existing.status === "failed")
        return { response: error(502, "request_failed", "该试卷解析请求已扣额度并失败；请使用新的 request_id 重试。") };
      const age = Date.now() - Date.parse(existing.created_at || 0);
      if (existing.status === "pending" && Number.isFinite(age) && age > 10 * 60_000)
        await deleteRequest(env, requestId);
      else return { response: error(409, "request_in_progress", "相同的 request_id 正在处理中，请稍后重试。") };
    }

    const minuteAgo = new Date(Date.now() - 60_000).toISOString();
    const minute = await env.DB.prepare(
      "SELECT COUNT(*) AS count FROM requests WHERE device_hash = ? AND created_at > ?"
    ).bind(deviceHash, minuteAgo).first();
    if ((minute?.count || 0) >= DEVICE_MINUTE_LIMIT)
      return { response: quotaError("rate_limited", "请求过于频繁，请稍后再试。", 0, resetAt) };

    await env.DB.prepare(
      "INSERT INTO requests(request_id, device_hash, ip_hash, status, created_at) VALUES(?,?,?,'pending',?)"
    ).bind(requestId, deviceHash, ipHash, new Date().toISOString()).run();
    inserted = true;
    const deviceCount = await reserve(env, day, `${service}:device`, deviceHash, DEVICE_DAILY_LIMIT);
    if (deviceCount === null) {
      await deleteRequest(env, requestId);
      return { response: quotaError("daily_quota_exceeded", "该服务今日设备额度已用完，请明天再试。", 0, resetAt) };
    }
    deviceReserved = true;
    const ipCount = await reserve(env, day, `${service}:ip`, ipHash, IP_DAILY_LIMIT);
    if (ipCount === null) {
      await release(env, day, `${service}:device`, deviceHash);
      await deleteRequest(env, requestId);
      return { response: quotaError("ip_quota_exceeded", "该服务当前网络今日额度已用完，请明天再试。", 0, resetAt) };
    }
    return { state: { requestId, deviceHash, ipHash, day, resetAt,
      remaining: Math.max(0, DEVICE_DAILY_LIMIT - deviceCount) } };
  } catch (err) {
    if (deviceReserved) await release(env, day, `${service}:device`, deviceHash);
    if (inserted) await deleteRequest(env, requestId);
    return { response: error(500, "database_error", `配额处理失败：${err.message}`) };
  }
}

async function completeQuotaRequest(env, state, responseJson) {
  await env.DB.prepare("UPDATE requests SET status = 'completed', response_json = ? WHERE request_id = ?")
    .bind(responseJson, state.requestId).run();
}

async function rollbackQuotaRequest(env, state) {
  await release(env, state.day, `${state.requestId.split(":")[0]}:device`, state.deviceHash);
  await release(env, state.day, `${state.requestId.split(":")[0]}:ip`, state.ipHash);
  await deleteRequest(env, state.requestId);
}

async function getAliyunToken(env) {
  if (aliyunTokenCache && aliyunTokenCache.expiresAt > Date.now() + 5 * 60_000)
    return aliyunTokenCache.value;
  const parameters = {
    AccessKeyId: env.ALIYUN_ACCESS_KEY_ID,
    Action: "CreateToken",
    Format: "JSON",
    RegionId: "cn-shanghai",
    SignatureMethod: "HMAC-SHA1",
    SignatureNonce: crypto.randomUUID(),
    SignatureVersion: "1.0",
    Timestamp: new Date().toISOString().replace(/\.\d{3}Z$/, "Z"),
    Version: "2019-02-28"
  };
  const canonical = Object.keys(parameters).sort()
    .map(key => `${rfc3986(key)}=${rfc3986(parameters[key])}`).join("&");
  const stringToSign = `GET&%2F&${rfc3986(canonical)}`;
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(`${env.ALIYUN_ACCESS_KEY_SECRET}&`),
    { name: "HMAC", hash: "SHA-1" }, false, ["sign"]);
  const signatureBytes = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(stringToSign));
  parameters.Signature = btoa(String.fromCharCode(...new Uint8Array(signatureBytes)));
  const query = Object.keys(parameters).sort()
    .map(name => `${rfc3986(name)}=${rfc3986(parameters[name])}`).join("&");
  const response = await fetch(`${ALIYUN_META_ENDPOINT}?${query}`, { headers: { "accept": "application/json" } });
  const raw = await response.text();
  if (!response.ok) throw new Error(`CreateToken HTTP ${response.status}: ${extractUpstreamMessage(raw)}`);
  let token;
  try { token = JSON.parse(raw)?.Token; } catch { throw new Error("CreateToken 返回了非 JSON 响应。"); }
  if (!token?.Id || !Number.isFinite(Number(token.ExpireTime))) throw new Error("CreateToken 未返回有效 Token。");
  aliyunTokenCache = { value: token.Id, expiresAt: Number(token.ExpireTime) * 1000 };
  return aliyunTokenCache.value;
}

async function synthesizeAliyunChunk(appKey, token, text) {
  const response = await fetch(ALIYUN_TTS_ENDPOINT, {
    method: "POST",
    headers: { "content-type": "application/json", "accept": "audio/wav" },
    body: JSON.stringify({ appkey: appKey, token, text, format: "wav", sample_rate: 16000,
      voice: "betty", volume: 50, speech_rate: 0, pitch_rate: 0 })
  });
  const bytes = new Uint8Array(await response.arrayBuffer());
  if (!response.ok || !isWave(bytes))
    throw new Error(`TTS HTTP ${response.status}: ${extractUpstreamMessage(new TextDecoder().decode(bytes))}`);
  return bytes;
}

function encodeAudioChunks(chunks) {
  const totalLength = 6 + chunks.reduce((sum, chunk) => sum + 4 + chunk.length, 0);
  const output = new Uint8Array(totalLength);
  const view = new DataView(output.buffer);
  output.set([69, 84, 83, 87], 0); // ETSW
  view.setUint16(4, chunks.length, true);
  let offset = 6;
  for (const chunk of chunks) {
    view.setUint32(offset, chunk.length, true); offset += 4;
    output.set(chunk, offset); offset += chunk.length;
  }
  return output;
}

function isWave(bytes) {
  return bytes.length >= 12 && bytes[0] === 82 && bytes[1] === 73 && bytes[2] === 70 && bytes[3] === 70 &&
    bytes[8] === 87 && bytes[9] === 65 && bytes[10] === 86 && bytes[11] === 69;
}

function rfc3986(value) {
  return encodeURIComponent(String(value)).replace(/[!'()*]/g, character =>
    `%${character.charCodeAt(0).toString(16).toUpperCase()}`);
}

function extractUpstreamMessage(raw) {
  try {
    const parsed = JSON.parse(raw);
    return String(parsed?.Message || parsed?.message || parsed?.error?.message || raw).slice(0, 300);
  } catch { return String(raw).replace(/[\r\n]/g, " ").slice(0, 300) || "未知错误"; }
}

function jsonText(payload) {
  return withCors(new Response(payload, { status: 200, headers: { "content-type": "application/json; charset=utf-8" } }));
}

async function solveWithDeepSeek(env, sections) {
  const system = [
    "You are an ETS listening answer engine.",
    "You receive listening transcripts and multiple-choice questions with options.",
    "Return ONLY a JSON object in this shape:",
    '{"answers":[{"section":1,"number":1,"answer":"A"}]}',
    "Every answer must be exactly one uppercase letter A, B, C or D.",
    "Do not include explanations, markdown, or extra keys."
  ].join("\n");

  const response = await fetch(DEEPSEEK_ENDPOINT, {
    method: "POST",
    headers: {
      "Authorization": `Bearer ${env.DEEPSEEK_API_KEY}`,
      "Content-Type": "application/json"
    },
    body: JSON.stringify({
      model: "deepseek-flash",
      temperature: 0.1,
      max_tokens: 1800,
      messages: [
        { role: "system", content: system },
        { role: "user", content: JSON.stringify({ sections }) }
      ]
    })
  });

  const text = await response.text();
  if (!response.ok) {
    let detail = text.slice(0, 300);
    try { detail = JSON.parse(text)?.error?.message || detail; } catch { /* keep text */ }
    throw new Error(`DeepSeek HTTP ${response.status}: ${detail}`);
  }

  let content = "";
  try {
    content = JSON.parse(text)?.choices?.[0]?.message?.content || "";
  } catch {
    throw new Error("DeepSeek 返回了非 JSON 响应。");
  }
  content = content.replace(/^```(?:json)?\s*/i, "").replace(/\s*```$/, "").trim();

  let parsed;
  try {
    parsed = JSON.parse(content);
  } catch {
    throw new Error("模型输出不是有效 JSON。");
  }

  const answers = Array.isArray(parsed?.answers) ? parsed.answers : [];
  if (answers.length === 0) throw new Error("模型没有返回答案。");

  const expected = new Map();
  sections.forEach((section, sectionIndex) => {
    for (const question of section.questions) {
      expected.set(`${sectionIndex + 1}:${question.number}`, question.options.length);
    }
  });
  const normalized = new Map();
  for (const item of answers) {
    const section = Number(item?.section);
    const number = Number(item?.number);
    const answer = String(item?.answer || "").trim().toUpperCase();
    if (!Number.isInteger(section) || !Number.isInteger(number)) continue;
    const key = `${section}:${number}`;
    const optionCount = expected.get(key);
    if (!optionCount || !/^[A-D]$/.test(answer) || "ABCD".indexOf(answer) >= optionCount) continue;
    normalized.set(key, { section, number, answer });
  }
  if (normalized.size !== expected.size) throw new Error("模型没有返回全部听力题答案。");
  return [...normalized.values()];
}

function validatePayload(body) {
  if (!body || typeof body !== "object") return { error: "请求体必须是对象。" };
  const sections = body.sections;
  if (!Array.isArray(sections) || sections.length < 1 || sections.length > 10) {
    return { error: "sections 必须是 1-10 段听力。" };
  }

  let totalQuestions = 0;
  const normalized = [];
  for (const section of sections) {
    if (!section || typeof section !== "object") return { error: "section 必须是对象。" };
    const passage = String(section.passage || "").trim();
    if (passage.length < 20 || passage.length > 12_000) {
      return { error: "每段听力需要 passage（听力原文/字幕），长度 20-12000 字符。" };
    }
    const questions = section.questions;
    if (!Array.isArray(questions) || questions.length < 1 || questions.length > 20) {
      return { error: "每段听力需要 1-20 道题目。" };
    }

    const normalizedQuestions = [];
    const seenNumbers = new Set();
    for (const question of questions) {
      const number = Number(question?.number);
      if (!Number.isInteger(number) || number < 1 || number > 200) {
        return { error: "题目 number 必须是 1-200 的整数。" };
      }
      if (seenNumbers.has(number)) return { error: "同一段听力中的题目 number 不能重复。" };
      seenNumbers.add(number);
      const prompt = String(question?.prompt || "").trim();
      if (prompt.length < 2 || prompt.length > 1000) {
        return { error: `第 ${number} 题缺少题干。` };
      }
      const options = Array.isArray(question?.options) ? question.options : [];
      if (options.length < 2 || options.length > 4) {
        return { error: `第 ${number} 题需要 2-4 个选项。` };
      }
      const normalizedOptions = options.map(option => String(option || "").trim());
      if (normalizedOptions.some(option => option.length === 0 || option.length > 600)) {
        return { error: `第 ${number} 题的选项为空或过长。` };
      }
      normalizedQuestions.push({ number, prompt, options: normalizedOptions });
    }

    totalQuestions += normalizedQuestions.length;
    if (totalQuestions > 60) return { error: "一张试卷最多 60 道听力题。" };
    normalized.push({ passage, questions: normalizedQuestions });
  }
  return { sections: normalized };
}

async function readBodyLimited(request, maxBytes) {
  if (!request.body) return "";
  const reader = request.body.getReader();
  const chunks = [];
  let total = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > maxBytes) {
      await reader.cancel();
      const err = new Error("Request body is too large");
      err.code = "body_too_large";
      throw err;
    }
    chunks.push(value);
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return new TextDecoder().decode(bytes);
}

async function reserve(env, day, scope, key, limit) {
  const row = await env.DB.prepare(
    `INSERT INTO daily_usage(day, scope, key, count) VALUES(?,?,?,1)
     ON CONFLICT(day, scope, key) DO UPDATE SET count = count + 1 WHERE count < ?
     RETURNING count`
  ).bind(day, scope, key, limit).first();
  return row?.count ?? null;
}

async function release(env, day, scope, key) {
  try {
    await env.DB.prepare(
      "UPDATE daily_usage SET count = MAX(count - 1, 0) WHERE day = ? AND scope = ? AND key = ?"
    ).bind(day, scope, key).run();
  } catch {
    // Best-effort compensation.
  }
}

async function deleteRequest(env, requestId) {
  try {
    await env.DB.prepare("DELETE FROM requests WHERE request_id = ?").bind(requestId).run();
  } catch {
    // Best-effort cleanup.
  }
}

async function sha256(value) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return [...new Uint8Array(digest)].map(byte => byte.toString(16).padStart(2, "0")).join("");
}

function shanghaiDay() {
  const shifted = new Date(Date.now() + 8 * 60 * 60 * 1000);
  const day = shifted.toISOString().slice(0, 10);
  const nextUtc = Date.UTC(
    shifted.getUTCFullYear(),
    shifted.getUTCMonth(),
    shifted.getUTCDate() + 1
  ) - 8 * 60 * 60 * 1000;
  return { day, resetAt: new Date(nextUtc).toISOString() };
}

function quotaError(code, message, remaining, resetAt) {
  return withCors(new Response(JSON.stringify({
    error: code,
    message,
    remaining_today: remaining,
    reset_at: resetAt
  }), {
    status: 429,
    headers: { "content-type": "application/json; charset=utf-8", "retry-after": "3600" }
  }));
}

function error(status, code, message) {
  return withCors(new Response(JSON.stringify({ error: code, message }), {
    status,
    headers: { "content-type": "application/json; charset=utf-8" }
  }));
}

function json(value) {
  return withCors(new Response(JSON.stringify(value), {
    status: 200,
    headers: { "content-type": "application/json; charset=utf-8" }
  }));
}

function withCors(response) {
  const headers = new Headers(response.headers);
  headers.set("access-control-allow-origin", "*");
  headers.set("access-control-allow-methods", "POST, GET, OPTIONS");
  headers.set("access-control-allow-headers", "content-type, x-device-id");
  headers.set("access-control-expose-headers", "x-remaining-today");
  return new Response(response.body, { status: response.status, headers });
}
