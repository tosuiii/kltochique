# EmpresaMonitor V2 — Live

Esta versão troca o envio de screenshots por requisições HTTP por uma conexão
WebSocket persistente. O objetivo é visualização muito mais fluida entre PCs em
redes diferentes.

## Estrutura

- `Panel/` — painel estático para Netlify.
- `RealtimeServer/` — servidor Node.js + WebSocket para Render/Railway/VPS.
- `Agent/` — aplicativo Windows.

## Fluxo

PC remoto -> WebSocket persistente -> RealtimeServer -> navegador administrador

O Agent captura a tela em aproximadamente:
- 1280 px de largura
- 12 FPS
- JPEG qualidade 68

Esses valores foram escolhidos para um primeiro teste equilibrado.

## 1. Hospedar RealtimeServer

### Render
Crie um Web Service apontando para a pasta `RealtimeServer`.

Configuração:
- Build command: `npm install`
- Start command: `npm start`

Variáveis de ambiente:
- `ADMIN_TOKEN`
- `AGENT_KEY`

Use as mesmas chaves do seu teste anterior ou crie novas.

Depois o Render dará uma URL HTTPS, por exemplo:

https://empresamonitor-realtime.onrender.com

No Agent e no painel use a versão WebSocket segura:

wss://empresamonitor-realtime.onrender.com

## 2. Hospedar Panel no Netlify

Você pode criar um novo site apontando para `Panel/`.

Ou copiar o `index.html` da pasta `Panel` para o site Netlify existente.

O painel pedirá:
- URL WSS do servidor realtime
- ADMIN_TOKEN

## 3. Gerar Agent.exe

No seu Windows com .NET 8 SDK:

Agent\BUILD_AGENT.bat

Informe:
- `wss://SEU-SERVIDOR-REALTIME.onrender.com`
- a mesma `AGENT_KEY` do servidor

Será criado:

Agent\publish\EmpresaMonitor.Agent.exe

## 4. Teste

No PC remoto:
1. Abra `EmpresaMonitor.Agent.exe`.
2. Ele mostra uma janela de status visível.
3. No painel, clique `Solicitar acesso`.
4. O PC remoto recebe a confirmação.
5. Após clicar `Sim`, no painel clique `Ver ao vivo`.
6. O Agent passa a transmitir continuamente.

O PC remoto possui botão `Encerrar compartilhamento`.

## Limitações desta V2

A transmissão é "live" sobre WebSocket usando JPEGs comprimidos, não H.264/WebRTC.
Mesmo assim, deve ser muito mais fluida que a versão anterior porque usa uma
conexão persistente e elimina o ciclo Function -> Blob -> polling.

Para uma futura V3:
- WebRTC/H.264 ou VP8
- 20–30 FPS adaptativos
- áudio
- TURN para redes restritivas
- controle remoto de mouse/teclado sob consentimento explícito

Esta versão NÃO implementa:
- controle de mouse/teclado
- keylogger
- captura de senhas
- execução oculta
- inicialização automática
