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

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === "OPTIONS") return withCors(new Response(null, { status: 204 }));

    if (url.pathname === "/health") {
      return json({
        ok: true,
        configured: Boolean(env.DEEPSEEK_API_KEY && env.DEVICE_HASH_SALT && env.IP_HASH_SALT),
        limits: { deviceDaily: DEVICE_DAILY_LIMIT, ipDaily: IP_DAILY_LIMIT }
      });
    }

    if (url.pathname !== "/v1/listening/solve") return error(404, "not_found", "接口不存在。");
    if (request.method !== "POST") return error(405, "method_not_allowed", "只支持 POST。");
    if (!env.DEEPSEEK_API_KEY) return error(500, "server_not_configured", "服务端尚未配置 DeepSeek Key。");
    if (!env.DB) return error(500, "server_not_configured", "服务端尚未绑定 D1 数据库。");

    const deviceId = (request.headers.get("X-Device-Id") || "").trim();
    if (!/^[A-Za-z0-9._:-]{16,160}$/.test(deviceId)) {
      return error(400, "invalid_device_id", "缺少或无效的 X-Device-Id（至少 16 个字符）。");
    }

    const contentLength = Number(request.headers.get("content-length") || 0);
    if (contentLength > MAX_BODY_BYTES) return error(413, "body_too_large", "请求体过大。");

    let body;
    try {
      body = await request.json();
    } catch {
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
        "SELECT status, response_json, created_at FROM requests WHERE request_id = ?"
      ).bind(requestId).first();
      if (existing) {
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

    const deviceCount = await reserve(env, day, "device", deviceHash, DEVICE_DAILY_LIMIT);
    if (deviceCount === null) {
      await deleteRequest(env, requestId);
      return quotaError("daily_quota_exceeded", "今日免费额度已用完，请填写自己的 API Key 或明天再试。", 0, resetAt);
    }

    const ipCount = await reserve(env, day, "ip", ipHash, IP_DAILY_LIMIT);
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

  const normalized = [];
  for (const item of answers) {
    const section = Number(item?.section);
    const number = Number(item?.number);
    const answer = String(item?.answer || "").trim().toUpperCase();
    if (!Number.isInteger(section) || !Number.isInteger(number)) continue;
    if (!/^[A-D]$/.test(answer)) continue;
    normalized.push({ section, number, answer });
  }
  if (normalized.length === 0) throw new Error("模型没有返回有效选项。");
  return normalized;
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
    for (const question of questions) {
      const number = Number(question?.number);
      if (!Number.isInteger(number) || number < 1 || number > 200) {
        return { error: "题目 number 必须是 1-200 的整数。" };
      }
      const prompt = String(question?.prompt || "").trim();
      if (prompt.length < 2 || prompt.length > 1000) {
        return { error: `第 ${number} 题缺少题干。` };
      }
      const options = Array.isArray(question?.options) ? question.options : [];
      if (options.length < 2 || options.length > 6) {
        return { error: `第 ${number} 题需要 2-6 个选项。` };
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
  return new Response(response.body, { status: response.status, headers });
}
