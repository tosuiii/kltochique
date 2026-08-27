import express from "express";
import http from "http";
import crypto from "crypto";
import { WebSocketServer, WebSocket } from "ws";

const PORT = Number(process.env.PORT || 8080);
const ADMIN_TOKEN = process.env.ADMIN_TOKEN || "troque-admin";
const AGENT_KEY = process.env.AGENT_KEY || "troque-agent";

const app = express();
app.get("/", (_req, res) => res.json({ status: "ok", service: "EmpresaMonitor Realtime V2" }));
app.get("/health", (_req, res) => res.json({ status: "ok", time: new Date().toISOString() }));

const server = http.createServer(app);
const wss = new WebSocketServer({
  server,
  maxPayload: 8 * 1024 * 1024
});

const agents = new Map(); // id -> { ws, id, name, user, active, watchers:Set }
const admins = new Set(); // ws objects

function send(ws, obj) {
  if (ws?.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(obj));
  }
}

function agentSummary(a) {
  return {
    id: a.id,
    name: a.name,
    user: a.user,
    accessActive: !!a.active,
    watchers: a.watchers.size
  };
}

function broadcastAgentList() {
  const list = [...agents.values()].map(agentSummary);
  for (const admin of admins) {
    send(admin, { type: "agent_list", agents: list });
  }
}

function stopStreamingIfUnused(agent) {
  if (agent.watchers.size === 0) {
    send(agent.ws, { type: "stream_stop" });
  }
}

wss.on("connection", (ws) => {
  ws.id = crypto.randomUUID();
  ws.role = "unknown";
  ws.selectedAgentId = null;

  ws.on("message", (data, isBinary) => {
    // Binary frames only come from an authenticated agent.
    if (isBinary) {
      if (ws.role !== "agent" || !ws.agentId) return;

      const agent = agents.get(ws.agentId);
      if (!agent || !agent.active) return;

      for (const admin of agent.watchers) {
        if (admin.readyState === WebSocket.OPEN && admin.selectedAgentId === agent.id) {
          admin.send(data, { binary: true });
        }
      }
      return;
    }

    let msg;
    try {
      msg = JSON.parse(data.toString());
    } catch {
      return;
    }

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

      agents.set(ws.agentId, {
        ws,
        id: ws.agentId,
        name: String(msg.name || "PC"),
        user: String(msg.user || ""),
        active: false,
        watchers: new Set()
      });

      send(ws, { type: "agent_ready", id: ws.agentId });
      broadcastAgentList();
      return;
    }

    if (msg.type === "admin_hello") {
      if (msg.token !== ADMIN_TOKEN) {
        send(ws, { type: "error", message: "ADMIN_TOKEN inválido" });
        ws.close();
        return;
      }

      ws.role = "admin";
      admins.add(ws);
      send(ws, {
        type: "admin_ready",
        agents: [...agents.values()].map(agentSummary)
      });
      return;
    }

    if (ws.role === "admin") {
      if (msg.type === "request_access") {
        const agent = agents.get(String(msg.agentId || ""));
        if (!agent) {
          send(ws, { type: "error", message: "Computador não encontrado" });
          return;
        }

        send(agent.ws, { type: "access_request" });
        send(ws, { type: "request_sent", agentId: agent.id });
        return;
      }

      if (msg.type === "watch") {
        const id = String(msg.agentId || "");

        // Remove the admin from any previous watcher set.
        if (ws.selectedAgentId) {
          const old = agents.get(ws.selectedAgentId);
          if (old) {
            old.watchers.delete(ws);
            stopStreamingIfUnused(old);
          }
        }

        const agent = agents.get(id);
        if (!agent) return;

        ws.selectedAgentId = id;
        agent.watchers.add(ws);

        if (agent.active) {
          send(agent.ws, { type: "stream_start" });
        }

        broadcastAgentList();
        return;
      }

      if (msg.type === "end_access") {
        const agent = agents.get(String(msg.agentId || ""));
        if (!agent) return;

        agent.active = false;
        send(agent.ws, { type: "access_ended" });
        send(agent.ws, { type: "stream_stop" });

        for (const admin of agent.watchers) {
          send(admin, { type: "access_state", agentId: agent.id, active: false });
        }

        broadcastAgentList();
        return;
      }
    }

    if (ws.role === "agent") {
      const agent = agents.get(ws.agentId);
      if (!agent) return;

      if (msg.type === "access_response") {
        agent.active = msg.allow === true;

        for (const admin of admins) {
          send(admin, {
            type: "access_state",
            agentId: agent.id,
            active: agent.active
          });
        }

        if (agent.active && agent.watchers.size > 0) {
          send(agent.ws, { type: "stream_start" });
        } else if (!agent.active) {
          send(agent.ws, { type: "stream_stop" });
        }

        broadcastAgentList();
        return;
      }

      if (msg.type === "end_access") {
        agent.active = false;
        send(agent.ws, { type: "stream_stop" });

        for (const admin of admins) {
          send(admin, {
            type: "access_state",
            agentId: agent.id,
            active: false
          });
        }

        broadcastAgentList();
      }
    }
  });

  ws.on("close", () => {
    if (ws.role === "admin") {
      admins.delete(ws);

      if (ws.selectedAgentId) {
        const agent = agents.get(ws.selectedAgentId);
        if (agent) {
          agent.watchers.delete(ws);
          stopStreamingIfUnused(agent);
        }
      }

      broadcastAgentList();
      return;
    }

    if (ws.role === "agent" && ws.agentId) {
      const agent = agents.get(ws.agentId);
      if (agent?.ws === ws) {
        for (const admin of agent.watchers) {
          send(admin, { type: "agent_offline", agentId: agent.id });
        }
        agents.delete(ws.agentId);
        broadcastAgentList();
      }
    }
  });
});

server.listen(PORT, "0.0.0.0", () => {
  console.log(`EmpresaMonitor Realtime V2 listening on ${PORT}`);
});
