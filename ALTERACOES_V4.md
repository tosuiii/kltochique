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
