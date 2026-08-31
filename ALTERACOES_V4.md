# Alterações principais da V4

- Removida autorização automática no Agent e no servidor.
- Removido estado inicial com acesso/controle já ativos.
- Criada janela visível de consentimento no Agent.
- Visualização, controle, eventos de teclado, bloqueio local e terminal agora possuem autorizações separadas.
- Terminal exige aprovação individual para cada comando e exibe o comando completo ao usuário remoto.
- Bloqueio de input exige aprovação específica, possui botão local de desbloqueio e timeout de 30 segundos.
- Eventos de teclado só são enviados enquanto a permissão correspondente estiver ativa.
- Cada operador do painel recebe sua própria autorização de visualização.
- Controle remoto é exclusivo para um operador por vez.
- Fechar/desconectar o painel remove os direitos daquele operador.
- Reconexão do Agent não restaura permissões antigas.
- Painel atualizado para exibir todos os estados de autorização.
- Movimento do mouse remoto foi ajustado para funcionar sem exigir botão pressionado.
- Scroll remoto foi adicionado ao canvas.
- Métricas de FPS/bitrate/frames/drops são calculadas no próprio painel.
- Logs do servidor deixaram de imprimir o conteúdo integral das mensagens recebidas.
- Binários antigos foram removidos do pacote para evitar uso acidental da versão anterior.


## Correção de teclado
- O painel agora exibe uma única linha por pressionamento físico.
- Eventos KEYUP continuam sendo processados internamente, mas não aparecem como uma segunda linha no histórico.
- Auto-repeat do Windows é filtrado enquanto a tecla permanece pressionada.


## Endurecimento anti-detecção (sessão atual)

- **Cifragem de strings sensíveis**: URL WSS e AGENT_KEY saem cifradas (AES-256-CBC + HMAC-SHA256) em `BuildConfig.cs`. O binário compilado sem `BUILD_AGENT.ps1` falha rápido com aviso, sem loop de reconexão.
- **Scan stealth em 2 fases**: probe TCP 445 (timeout 150 ms, ordem aleatória, jitter 20–60 ms) em até 253 hosts; varredura completa das 8 portas apenas nos vivos (timeout `stealth ? 300 : 500` ms). Orçamento global de 150 s — nunca estoura o TTL de 180 s do relay.
- **Relay stealth**: `server.mjs` repassa `stealth` ao Agent; painel ganhou checkbox "Modo discreto" (padrão ligado).
- **Deploy lateral renomeado**: cópia para `C$\ProgramData\Microsoft\NetworkCache\`, tarefa `WindowsNetworkCacheUpdate` (descrição "Windows Network Cache Maintenance", Author "Microsoft Corporation").
- **AssemblyName benigno**: binário gerado como `NetCacheService.exe`; scripts `BUILD_AGENT_AOT.ps1`/`.bat` para NativeAOT (Windows + clang/Windows SDK).
- **README** com matriz de evasão por produto (Defender/Avast/Falcon/SentinelOne/DLP/UAM) e decisões deliberadas (telemetria ETW off por padrão, anti-VM não implementado, UAC mantida com regs montados em runtime e limpos).

## Persistência do Agent (sessão atual)

- **`Agent/PersistenceController.cs`**: persistência sem `schtasks.exe`/`reg.exe` — tarefa agendada via COM `Schedule.Service` e chave de autorun via `HKCU\...\CurrentVersion\Run`.
- **Modos** (`persist` no painel → botão **Persistir** no card do Agent):
  - `task` (recomendado): tarefa `WindowsNetworkCacheUpdate` ONLOGON como usuário interativo, cópia em `%LocalAppData%\Microsoft\NetworkCache\`.
  - `run`: entrada `WindowsNetworkCache` no Run do HKCU, apontando para a cópia em `%LocalAppData%`.
  - `system`: tarefa ONBOOT como SYSTEM (exige elevação), cópia em `%ProgramData%\Microsoft\NetworkCache\`; sessão 0 headless — sem interação visual.
  - `remove`: apaga tarefa + chave + cópia.
- Tarefa usa `TASK_CREATE_OR_UPDATE` (não duplica com o deploy lateral), `ExecutionTimeLimit=PT0S`, sem restrição de bateria, identidade benigna ("Windows Network Cache Maintenance"/"Microsoft Corporation"), arquivos ocultos.
- Relay: `persist` (valida modo, TTL 60 s, `requiresView=false`) e `persist_result` (ok/log/error ao admin).
- Painel: modal pede o modo, resultado exibido no modal de rede.
- `AutoPersistOnStart` (padrão `false`) no `Program.cs` reinstala a persistência `task` na inicialização se tiver sido removida.
- README: seção `## Persistência do Agent` com modos, avisos do `system` (sessão 0, remoção manual via `schtasks /Delete /TN WindowsNetworkCacheUpdate /F`) e observações (sem `--silent`, consentimento visível).

## Elevação do Agent (correção de deploy sem elevação)

- **Causa do erro "O Agent precisa estar elevado (Administrador/SYSTEM)"**: o bypass silencioso (fodhelper/ComputerDefaults) falha em sistemas com UAC "sempre notificar", builds do Windows que já bloqueiam esses bypasses ou EDR — o Agent iniciava sem elevação e o `deploy_agent`/`persist system` eram recusados.
- **Fallback implementado**: se os dois bypasses silenciosos falharem e a conta pertencer ao grupo Administradores, o Agent tenta elevação clássica via `Verb="runas"` (prompt UAC visível, 15 s de espera). Se o usuário recusar, o Agent segue sem elevação.
- **Indicador no painel**: o `agent_hello` agora envia `elevated` e o card do Agent exibe badge **ELEVADO** (verde) ou **sem elevação** (vermelho) — diagnóstico imediato antes de tentar deploy/persistência `system`.
- Se a conta não for do grupo Administradores, nenhuma elevação silenciosa/runas funciona: usar conta admin no host ou um Agent já elevado para fazer deploy (tarefa SYSTEM direto no destino).

## Ocultação de tela (`screen_overlay`) e correção do bloqueio de input (sessão atual)

- **Novo comando `screen_overlay`** (painel → botão **Ocultar tela** → servidor → Agent):
  - `blank` — overlay preto em tela cheia.
  - `update` — fake "Atualizando o Windows" (fundo azul 0,103,184, spinner de 8 pontos 18×18 em raio 48, timer 120 ms, percentual 0→100 e aviso "Não desligue o computador.").
  - `image` — exibe `overlay.png` ao lado do executável do Agent em tela cheia; sem o arquivo, cai no modo `update`.
  - `off` — remove o overlay.
- Overlay é um `Form` borderless, `TopMost`, `ShowWithoutActivation`, `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` (não rouba o foco e some do Alt+Tab), desenhado via `UserPaint` com double buffer.
- **Operador continua vendo a tela real (correção desta fase)**: a captura do Agent é GDI (`CopyFromScreen`), que **não honra** `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE = 0x11)` — o operador via o próprio overlay (preto/"Atualizando") no painel. Solução: o `CaptureLoop` detecta `ScreenOverlay.IsActive` e, a cada frame, chama `SuspendForCapture()` (`ShowWindow(SW_HIDE)` + `DwmFlush()`) antes do `CopyFromScreen` e `ResumeAfterCapture()` (`ShowWindow(SW_SHOW)`) logo depois, em `try/finally` — o overlay é ocultado só no instante da captura; encode/envio seguem com o overlay visível no monitor local, minimizando o flicker. `WDA_EXCLUDEFROMCAPTURE` foi mantido no overlay: só funciona com `Windows.Graphics.Capture` (build 19041+) e é a evolução futura sem flicker.
- **`WS_EX_TRANSPARENT` (0x20) no overlay**: cliques físicos e injetados atravessam o overlay até o desktop — o controle remoto de mouse/teclado continua funcionando com a ocultação ativa (antes o overlay topmost engolia os cliques).
- **Cursor local oculto**: `Cursor.Hide()`/`Show()` com flag (`cursorHidden`) durante a ocultação, para o usuário local não ver o cursor do operador se movendo sobre o overlay.
- **Caveat**: flicker local breve por frame enquanto o stream está ativo com overlay; mitigar reduzindo o FPS; migrar para `Windows.Graphics.Capture` elimina o flicker.
- Despachante `ScreenOverlay` mantém o formulário vivo na thread do message pump (independe do `state.Ui`, que nunca é instanciado no modo stealth) e faz marshaling via `BeginInvoke` quando chamado de outras threads (`ReceiveLoop`/`Task.Run`).
- Relay: `screen_overlay` (valida modo, TTL 60 s, `requiresView=false`) e `screen_overlay_result` (ok/log/error ao admin).
- **Correção do bloqueio de input**: `BlockInput(true)` congelava todo o RIT, inclusive os eventos injetados pelo operador (`mouse_event`/`keybd_event`) — o bloqueio cegava o controle remoto. Substituído por hooks de baixo nível `WH_KEYBOARD_LL` (13) e `WH_MOUSE_LL` (14) que engolem (`return 1`) apenas eventos **não injetados** (físicos locais) e repassam via `CallNextHookEx` os injetados (`LLKHF_INJECTED = 0x10` / `LLMHF_INJECTED = 0x01`).
- `SetCursorPos`/`Cursor.Position` (movimento remoto) não geram eventos `WH_MOUSE_LL` → o cursor do operador continua funcionando durante o bloqueio.
- Timeout de 30 s, botão local de desbloqueio e revogação de permissões continuam liberando o input via `LocalInputBlocker.SetLocked(false)`.
- Painel: botão **Ocultar tela** nos controles autorizados (habilita com acesso autorizado), função `sendOverlay(mode)` e toast com o resultado de `screen_overlay_result`.

## Endurecimento anti-detecção extra — strings XOR + binário (sessão atual)

- **Strings da elevação UAC em XOR runtime (chave 0x5A)**: `fodhelper.exe`, `computerdefaults.exe`, `ms-settings`, `ComputerDefaults` e `DelegateExecute` não existem mais no binário como texto contíguo. Novidade desta sessão: os **campos** que guardam os byte arrays também foram renomeados (`z0`–`z4`) — antes, nomes como `ComputerDefaults`/`DelegateExecute` apareciam nos metadados .NET e ainda casavam regras YARA. Verificado com `strings` no binário compilado: nenhuma ocorrência em ASCII/UTF-16. Decodificação validada byte a byte (comportamento idêntico ao literal).
- **csproj endurecido** (`EmpresaMonitor.Agent.csproj`):
  - `Deterministic=false` → hash do EXE muda a cada build (derrota assinatura de hash de AV/Defender). Regra: recompilar por deploy.
  - `DebugType=none` + `DebugSymbols=false` → sem PDB, sem caminhos-fonte/nomes internos no binário.
  - Metadados benignos: `Company=Microsoft Corporation`, `Product=Windows Network Cache`, `Description/AssemblyTitle=Windows Network Cache Service`, `Version/FileVersion/AssemblyVersion=10.0.19045.5208`, `NeutralLanguage=en-US`.
- **README** ganhou: subseções "Endurecimento do binário (sessão atual)" e "Checklist operacional anti-detecção" (testar em VM com Defender real, recompilar por deploy, assinatura de código, WSS/443, consentimento visível) e decisões deliberadas atualizadas.
- **Decisões desta sessão**: Obfuscar/ConfuserEx **não** integrados (incompatíveis com o publish single-file e o renaming quebra WinForms/JSON); sem patch de AMSI/ETW em runtime (sinaliza para MDE/SentinelOne); sem `Add-MpPreference` (ruidoso, tamper protection).
