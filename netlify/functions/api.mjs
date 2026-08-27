import { getStore } from "@netlify/blobs";

const store = getStore("empresamonitor");

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

function computerKey(id) {
  return `computer:${id}`;
}

function frameKey(id) {
  return `frame:${id}`;
}

async function getComputer(id) {
  const raw = await store.get(computerKey(id), { type: "text" });
  return raw ? JSON.parse(raw) : null;
}

async function setComputer(pc) {
  await store.set(computerKey(pc.id), JSON.stringify(pc));
}

function apiPath(event) {
  // Works with the Netlify redirect /api/* -> /.netlify/functions/api/:splat
  const raw =
    event.path ||
    event.rawUrl ||
    "";

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

      const old = await getComputer(id);

      const pc = {
        id,
        name: body.name || old?.name || "PC",
        user: body.user || old?.user || "",
        lastSeen: new Date().toISOString(),
        accessRequested: old?.accessRequested || false,
        accessActive: old?.accessActive || false
      };

      await setComputer(pc);
      return json(200, { id });
    }

    if (method === "POST" && path === "/heartbeat") {
      if (!agentOk(event)) return unauthorized();

      const body = JSON.parse(event.body || "{}");
      const pc = await getComputer(body.id);

      if (pc) {
        pc.user = body.user || pc.user;
        pc.lastSeen = new Date().toISOString();
        await setComputer(pc);
      }

      return json(200, { ok: true });
    }

    if (method === "GET" && path === "/computers") {
      if (!adminOk(event)) return unauthorized();

      const { blobs } = await store.list({ prefix: "computer:" });
      const pcs = [];

      for (const blob of blobs) {
        const raw = await store.get(blob.key, { type: "text" });
        if (raw) pcs.push(JSON.parse(raw));
      }

      pcs.sort((a, b) => (a.name || "").localeCompare(b.name || ""));
      return json(200, pcs);
    }

    if (
      parts[0] === "computers" &&
      parts[1] &&
      parts[2] === "request-access" &&
      method === "POST"
    ) {
      if (!adminOk(event)) return unauthorized();

      const pc = await getComputer(parts[1]);
      if (!pc) return json(404, { error: "not_found" });

      pc.accessRequested = true;
      await setComputer(pc);

      return json(200, { ok: true });
    }

    if (
      parts[0] === "computers" &&
      parts[1] &&
      parts[2] === "authorize" &&
      method === "POST"
    ) {
      if (!agentOk(event)) return unauthorized();

      const pc = await getComputer(parts[1]);
      if (!pc) return json(404, { error: "not_found" });

      const body = JSON.parse(event.body || "{}");
      pc.accessRequested = false;
      pc.accessActive = body.allow === true;

      await setComputer(pc);
      return json(200, { ok: true });
    }

    if (
      parts[0] === "computers" &&
      parts[1] &&
      parts[2] === "access-state" &&
      method === "GET"
    ) {
      if (!agentOk(event)) return unauthorized();

      const pc = await getComputer(parts[1]);
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

      const pc = await getComputer(parts[1]);
      if (!pc) return json(404, { error: "not_found" });

      pc.accessRequested = false;
      pc.accessActive = false;
      await setComputer(pc);

      return json(200, { ok: true });
    }

    if (
      parts[0] === "computers" &&
      parts[1] &&
      parts[2] === "frame" &&
      method === "POST"
    ) {
      if (!agentOk(event)) return unauthorized();

      const pc = await getComputer(parts[1]);
      if (!pc || pc.accessActive !== true)
        return json(403, { error: "access_inactive" });

      const rawBody = event.body || "";
      const base64 = event.isBase64Encoded
        ? rawBody
        : Buffer.from(rawBody, "binary").toString("base64");

      await store.set(frameKey(parts[1]), base64);

      return json(200, { ok: true });
    }

    if (
      parts[0] === "computers" &&
      parts[1] &&
      parts[2] === "frame" &&
      method === "GET"
    ) {
      if (!adminOk(event)) return unauthorized();

      const base64 = await store.get(frameKey(parts[1]), { type: "text" });

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
