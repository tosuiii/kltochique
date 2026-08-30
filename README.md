# KL TOCHIQUE — V4.2 Cyber Consent

Versão reorganizada para suporte remoto com **consentimento explícito e revogável**. Nenhuma permissão sensível nasce ativa e o servidor não pré-autoriza sessões.

## Fluxo de consentimento

As permissões são separadas:

1. **Visualização da tela** — o operador solicita e o usuário do Agent aceita ou nega localmente.
2. **Controle de mouse/teclado** — exige visualização autorizada e uma segunda confirmação local.
3. **Eventos de teclado** — exige confirmação própria e fica claramente indicado no Agent enquanto estiver ativo.
4. **Bloqueio temporário de teclado/mouse local** — exige controle autorizado e uma confirmação específica. Possui desbloqueio local e timeout automático de 3 minutos.
5. **Modo de manutenção com tela protegida** — exige confirmação própria. Exibe uma cortina preta com aviso e contador no PC remoto, bloqueia teclado/mouse por até 3 minutos e pausa stream, controle e comandos remotos durante esse período.
6. **Terminal remoto** — exige controle autorizado e **cada comando individual** é exibido integralmente no computador remoto para aprovação antes da execução.

O usuário do Agent pode revogar controle, eventos de teclado, desbloquear o input ou encerrar todo o compartilhamento a qualquer momento.

## Melhorias de segurança

- Todas as permissões começam em `false`.
- Reconexão ou queda de conexão revoga as permissões locais.
- O servidor ignora qualquer tentativa de `sessionAuthorized` automático.
- Autorização de visualização é associada ao operador autenticado que fez a solicitação.
- Controle remoto possui um único operador proprietário por vez.
- Eventos de teclado são enviados apenas ao operador que recebeu a autorização correspondente.
- Um operador não pode controlar, bloquear input ou enviar comandos sem ter autorização de visualização.
- Comandos remotos exigem também controle autorizado e aprovação local por comando.
- O Agent possui uma janela visível e permanente com os estados das permissões e botões de revogação.
- Fechamento do painel remove os direitos daquele operador.
- Fechamento/reconexão do Agent desbloqueia teclado/mouse local e encerra a cortina de manutenção.
- A cortina de manutenção não é excluída da captura para criar operação oculta: enquanto ativa, o stream e o controle remoto ficam pausados.
- O servidor não registra no console o conteúdo integral dos comandos recebidos.

## Arquivos principais

- `Agent/Program.cs` — Agent Windows, UI de consentimento, captura de tela e controle autorizado.
- `Agent/BuildConfig.cs` — URL WSS e chave do Agent.
- `Agent/BUILD_AGENT.bat` / `.ps1` — compilação self-contained para Windows x64.
- `RealtimeServer/server.mjs` — roteamento WebSocket e estado de autorização por operador.
- `Panel/index.html` — painel web.

## Deploy do servidor

Configure obrigatoriamente estas variáveis de ambiente no Railway/Render:

- `ADMIN_TOKEN` — senha usada pelo painel.
- `AGENT_KEY` — chave compartilhada usada pelos Agents.
- `PORT` — normalmente fornecida automaticamente pela plataforma.

O servidor agora **não inicia** se `ADMIN_TOKEN` ou `AGENT_KEY` estiverem vazias.

Depois do deploy, a raiz deve responder algo como:

```json
{"status":"ok","service":"KL TOCHIQUE Realtime","version":"4.2-cyber-consent"}
```

## Compilar o Agent

No Windows, dentro de `Agent`:

```bat
BUILD_AGENT.bat
```

O script solicita:

- URL WSS do servidor, por exemplo `wss://seu-projeto.up.railway.app`
- `AGENT_KEY` configurada no servidor

E gera:

`Agent/publish/EmpresaMonitor.Agent.exe`

Requer o SDK do .NET 8 instalado na máquina de compilação.

## Uso

1. Abra o Agent no computador remoto. A janela de consentimento deve permanecer visível.
2. Abra o painel e conecte usando a URL WSS, `ADMIN_TOKEN` e o nome do operador.
3. Clique em **Solicitar acesso**.
4. O usuário remoto aceita ou nega a visualização.
5. Depois da autorização, clique em **Ver ao vivo**.
6. Para controlar mouse/teclado, clique em **Solicitar controle** e aguarde a segunda aprovação local.
7. Eventos de teclado, bloqueio de input, modo de manutenção e terminal possuem autorizações adicionais conforme descrito acima.
8. O botão **Tela protegida — 3 min** exibe um aviso completo no PC remoto antes da ativação. Se aceito, o usuário vê uma tela preta identificada como manutenção autorizada e um contador. O painel pausa o feed/controle até o encerramento.

## Perfis de vídeo

- **Fluido** — 1280 px / 30 FPS / JPEG 55
- **Equilibrado** — 1600 px / 25 FPS / JPEG 62
- **Qualidade** — 1920 px / 20 FPS / JPEG 72

## Observações

O painel calcula localmente FPS, taxa de recebimento, total de frames e frames descartados. O vídeo continua usando JPEG sobre WebSocket; para redes com alta latência ou uso em escala, WebRTC é uma evolução futura mais adequada.

A versão entregue no ZIP contém o **código-fonte limpo**. Binários antigos foram removidos para evitar executar por engano uma versão anterior sem o novo fluxo de consentimento.


## Visual V4.2

O painel recebeu um rework inspirado em interfaces cyberpunk: HUD escuro, amarelo/ciano/magenta neon, recortes angulares, grade de fundo, scanlines discretas e cards de status. A mudança é apenas visual e preserva o fluxo de consentimento.
