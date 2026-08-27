# EmpresaMonitor — Netlify MVP

Projeto preparado para Git + Netlify.

## 1. Subir para o Git
Suba todo o conteúdo desta pasta para um repositório.

## 2. Criar o site no Netlify
No Netlify:
- Add new project / Import an existing project
- escolha o seu repositório Git
- o `netlify.toml` já define:
  - Publish directory: `site`
  - Functions directory: `netlify/functions`

## 3. Variáveis de ambiente
No Netlify, adicione:

ADMIN_TOKEN
AGENT_KEY

Use valores longos e diferentes.

Exemplo apenas para teste:
ADMIN_TOKEN = Admin-Teste-2026-9xA7
AGENT_KEY   = Agent-Teste-2026-4mB8

## 4. Fazer o deploy
Depois do deploy, teste:

https://SEU-SITE.netlify.app/api/health

Deve retornar JSON com `status: ok`.

## 5. Abrir o painel
Abra:

https://SEU-SITE.netlify.app

Digite o `ADMIN_TOKEN`.

## 6. Gerar o Agent.exe
No seu PC com .NET 8 SDK:

Agent\BUILD_AGENT.bat

Ele vai pedir:
1. URL do Netlify
2. AGENT_KEY

Depois cria:

Agent\publish\EmpresaMonitor.Agent.exe

Você distribui SOMENTE esse EXE.

## Fluxo no segundo PC
1. Dê dois cliques no EmpresaMonitor.Agent.exe.
2. O Agent conecta ao seu site Netlify automaticamente.
3. O computador aparece no painel.
4. Clique `Solicitar acesso`.
5. No segundo PC aparece a autorização.
6. Clique `Sim`.
7. No painel clique `Ver tela`.

## Importante
Este MVP usa screenshots periódicos, não streaming de vídeo.
Não possui controle de mouse/teclado.
Não possui keylogger, captura de senha ou execução oculta.

Para uso comercial, adicione autenticação de usuário, tokens individuais por
máquina, expiração de sessão, auditoria, política de retenção e revisão jurídica/LGPD.
