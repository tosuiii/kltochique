# EmpresaMonitor — Netlify MVP sem Blobs

Esta versão remove a dependência de Netlify Blobs para facilitar o teste.

## Importante
O estado fica apenas em memória na Function.
Se o Netlify reiniciar a Function, a lista de PCs e o último frame podem sumir.

Isso é aceitável para validar o fluxo:
Agent -> painel -> solicitar acesso -> autorizar -> visualizar tela.

## Deploy
Suba estes arquivos para o Git, substituindo a versão anterior.

No Netlify mantenha:
- Publish directory: `site`
- Functions directory: `netlify/functions`

Mantenha as variáveis:
- ADMIN_TOKEN
- AGENT_KEY

Depois faça um novo deploy.

Teste:
https://SEU-SITE.netlify.app/api/health

Deve retornar:
{"status":"ok","storage":"memory-test", ...}

## Agent
Depois rode:
Agent\BUILD_AGENT.bat

Informe:
1. URL do Netlify
2. AGENT_KEY

Será criado:
Agent\publish\EmpresaMonitor.Agent.exe
