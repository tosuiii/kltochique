const computers = globalThis.__EMPRESAMONITOR_COMPUTERS__ ||= new Map();
const frames = globalThis.__EMPRESAMONITOR_FRAMES__ ||= new Map();

function json(statusCode, data) {
  return {
    statusCode,
    headers: {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store"
    },
    body: JSON.stringify(data)
  };
}

function unauthorized() {
  return json(401, { error: "unauthorized" });
}

function adminOk(event) {
  const expected = process.env.ADMIN_TOKEN || "";
  const received =
    event.headers["x-admin-token"] ||
    event.headers["X-Admin-Token"] ||
    "";
  return expected && received === expected;
}

function agentOk(event) {
  const expected = process.env.AGENT_KEY || "";
  const received =
    event.headers["x-agent-key"] ||
    event.headers["X-Agent-Key"] ||
    "";
  return expected && received === expected;
}

function apiPath(event) {
  const raw = event.path || "";

  const marker = "/.netlify/functions/api/";
  const idx = raw.indexOf(marker);

  if (idx >= 0) {
    return "/" + raw.slice(idx + marker.length).split("?")[0];
  }

  const apiIdx = raw.indexOf("/api/");
  if (apiIdx >= 0) {
    return "/" + raw.slice(apiIdx + 5).split("?")[0];
  }

  return "/";
}

export async function handler(event) {
  try {
    const method = event.httpMethod;
    const path = apiPath(event);
    const parts = path.split("/").filter(Boolean);

    if (method === "GET" && path === "/health") {
      return json(200, {
        status: "ok",
        storage: "memory-test",
        time: new Date().toISOString()
      });
    }

    if (method === "POST" && path === "/register") {
      if (!agentOk(event)) return unauthorized();

      const body = JSON.parse(event.body || "{}");
      const id =
        body.id && String(body.id).trim()
          ? String(body.id).trim()
          : crypto.randomUUID().replaceAll("-", "");

      const old = computers.get(id);

      const pc = {
        id,
        name: body.name || old?.name || "PC",
        user: body.user || old?.user || "",
        lastSeen: new Date().toISOString(),
        accessRequested: old?.accessRequested || false,
        accessActive: old?.accessActive || false
      };

      computers.set(id, pc);
      return json(200, { id });
    }

    if (method === "POST" && path === "/heartbeat") {
      if (!agentOk(event)) return unauthorized();

      const body = JSON.parse(event.body || "{}");
      const pc = computers.get(body.id);

      if (pc) {
        pc.user = body.user || pc.user;
        pc.lastSeen = new Date().toISOString();
        computers.set(body.id, pc);
      }

      return json(200, { ok: true });
    }

    if (method === "GET" && path === "/computers") {
      if (!adminOk(event)) return unauthorized();

      const pcs = Array.from(computers.values())
        .sort((a, b) => (a.name || "").localeCompare(b.name || ""));

      return json(200, pcs);
    }

    if (
      parts[0] === "computers" &&
      parts[1] &&
      parts[2] === "request-access" &&
      method === "POST"
    ) {
      if (!adminOk(event)) return unauthorized();

      const pc = computers.get(parts[1]);
      if (!pc) return json(404, { error: "not_found" });

      pc.accessRequested = true;
      computers.set(parts[1], pc);
      return json(200, { ok: true });
    }

    if (
      parts[0] === "computers" &&
      parts[1] &&
      parts[2] === "authorize" &&
      method === "POST"
    ) {
      if (!agentOk(event)) return unauthorized();

      const pc = computers.get(parts[1]);
      if (!pc) return json(404, { error: "not_found" });

      const body = JSON.parse(event.body || "{}");
      pc.accessRequested = false;
      pc.accessActive = body.allow === true;
      computers.set(parts[1], pc);

      return json(200, { ok: true });
    }

    if (
      parts[0] === "computers" &&
      parts[1] &&
      parts[2] === "access-state" &&
      method === "GET"
    ) {
      if (!agentOk(event)) return unauthorized();

      const pc = computers.get(parts[1]);
      if (!pc) return json(404, { error: "not_found" });

      return json(200, {
        accessRequested: pc.accessRequested === true,
        accessActive: pc.accessActive === true
      });
    }

    if (
      parts[0] === "computers" &&
      parts[1] &&
      parts[2] === "end-access" &&
      method === "POST"
    ) {
      if (!adminOk(event)) return unauthorized();

      const pc = computers.get(parts[1]);
      if (!pc) return json(404, { error: "not_found" });

      pc.accessRequested = false;
      pc.accessActive = false;
      computers.set(parts[1], pc);

      return json(200, { ok: true });
    }

    if (
      parts[0] === "computers" &&
      parts[1] &&
      parts[2] === "frame" &&
      method === "POST"
    ) {
      if (!agentOk(event)) return unauthorized();

      const pc = computers.get(parts[1]);
      if (!pc || pc.accessActive !== true)
        return json(403, { error: "access_inactive" });

      const rawBody = event.body || "";
      const base64 = event.isBase64Encoded
        ? rawBody
        : Buffer.from(rawBody, "binary").toString("base64");

      frames.set(parts[1], base64);

      return json(200, { ok: true });
    }

    if (
      parts[0] === "computers" &&
      parts[1] &&
      parts[2] === "frame" &&
      method === "GET"
    ) {
      if (!adminOk(event)) return unauthorized();

      const base64 = frames.get(parts[1]);

      if (!base64)
        return json(404, { error: "frame_not_found" });

      return {
        statusCode: 200,
        headers: {
          "content-type": "image/jpeg",
          "cache-control": "no-store"
        },
        isBase64Encoded: true,
        body: base64
      };
    }

    return json(404, { error: "route_not_found", path, method });
  } catch (error) {
    console.error(error);
    return json(500, {
      error: "server_error",
      message: error?.message || "unknown"
    });
  }
}
