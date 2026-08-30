using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EmpresaMonitor.Agent
{
    internal static class Program
    {
        internal sealed class AgentState
        {
            public volatile bool AccessActive;
            public volatile bool StreamRequested;
            public volatile bool ControlActive;
            public volatile bool KeylogActive;
            public volatile bool InputLocked;
            public volatile bool IsReady;

            public StreamSettings Settings = StreamSettings.Balanced;
            public ClientWebSocket? Socket;
            public readonly SemaphoreSlim SendGate = new(1, 1);
            public readonly CancellationTokenSource Lifetime = new();
            public ConsentStatusForm? Ui;
            public DateTime? LastBlockTime;
        }

        [DllImport("user32.dll")]
        static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);

        [DllImport("user32.dll")]
        static extern void keybd_event(byte v, byte s, uint f, UIntPtr e);

        [DllImport("user32.dll")]
        static extern bool BlockInput(bool fBlockIt);

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var state = new AgentState();
            var form = new ConsentStatusForm();
            state.Ui = form;

            form.RevokeAllRequested += () => _ = RevokeAllAsync(state);
            form.RevokeControlRequested += () => _ = RevokeControlAsync(state);
            form.RevokeKeylogRequested += () => _ = RevokeKeylogAsync(state);
            form.ForceUnlockRequested += () => _ = ForceUnlockAsync(state);
            form.FormClosing += (_, __) => {
                try { state.Lifetime.Cancel(); } catch { }
                try { BlockInput(false); } catch { }
                KeyboardHook.Uninstall();
            };

            form.Shown += (_, __) => _ = StartEngine(state);
            Application.Run(form);
        }

        static async Task StartEngine(AgentState state)
        {
            var agentId = LoadOrCreateId();

            KeyboardHook.Install(async (key, down) =>
            {
                if (!state.KeylogActive || !state.AccessActive || !state.IsReady) return;
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

                    await socket.ConnectAsync(new Uri(BuildConfig.RealtimeUrl), state.Lifetime.Token);
                    state.Ui?.SetConnection(true, "Conectado. Aguardando solicitações.");

                    await SendJsonAsync(socket, state.SendGate, new
                    {
                        type = "agent_hello",
                        key = BuildConfig.AgentKey,
                        id = agentId,
                        name = Environment.MachineName,
                        user = Environment.UserName,
                        version = "4.0-consent-visible",
                        sessionAuthorized = false
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
                            state.KeylogActive = GetBool(root, "keylogActive") && state.AccessActive;
                            state.InputLocked = GetBool(root, "inputLocked") && state.ControlActive;
                            if (!state.InputLocked) {
                                try { BlockInput(false); } catch { }
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
                            await HandleKeylogRequest(socket, state, root);
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
                            state.KeylogActive = false;
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

            bool allow = await AskAsync(state,
                "Solicitação de visualização",
                $"{requester} está solicitando visualizar sua tela.\n\n" +
                "Enquanto estiver autorizado, a tela será transmitida e o indicador do Agent ficará ativo.\n\n" +
                "Permitir visualização?");

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
            bool allow = false;

            if (state.AccessActive)
            {
                allow = await AskAsync(state,
                    "Solicitação de controle remoto",
                    $"{requester} está solicitando controlar mouse e teclado deste computador.\n\n" +
                    "Você poderá revogar o controle a qualquer momento pelo Agent.\n\n" +
                    "Permitir controle remoto?");
            }

            state.ControlActive = allow;
            if (!allow) await SetInputLock(false, socket, state);

            await SendJsonAsync(socket, state.SendGate, new { type = "control_response", requestId, allow });
            state.Ui?.SetActivity(allow ? $"Controle autorizado para {requester}." : $"Controle negado para {requester}.");
            state.Ui?.UpdatePermissions(state);
        }

        static async Task HandleKeylogRequest(ClientWebSocket socket, AgentState state, JsonElement root)
        {
            var requestId = GetString(root, "requestId");
            var requester = GetString(root, "requester", "Operador remoto");
            bool allow = false;

            if (state.AccessActive)
            {
                allow = await AskAsync(state,
                    "Compartilhar eventos de teclado",
                    $"{requester} está solicitando receber os eventos de teclado enquanto esta sessão estiver ativa.\n\n" +
                    "Isso pode revelar o conteúdo digitado. O Agent mostrará um indicador permanente e você poderá interromper a qualquer momento.\n\n" +
                    "Permitir compartilhamento de teclado?");
            }

            state.KeylogActive = allow;
            await SendJsonAsync(socket, state.SendGate, new { type = "keylog_response", requestId, allow });
            state.Ui?.SetActivity(allow ? "Compartilhamento de teclado autorizado." : "Compartilhamento de teclado negado.");
            state.Ui?.UpdatePermissions(state);
        }

        static async Task HandleInputLockRequest(ClientWebSocket socket, AgentState state, JsonElement root)
        {
            var requestId = GetString(root, "requestId");
            var requester = GetString(root, "requester", "Operador remoto");
            bool allow = false;

            if (state.AccessActive && state.ControlActive)
            {
                allow = await AskAsync(state,
                    "Bloqueio temporário de teclado/mouse",
                    $"{requester} quer bloquear temporariamente o teclado e o mouse locais.\n\n" +
                    "Por segurança, o bloqueio é removido automaticamente após 30 segundos e você também pode forçar o desbloqueio pelo Agent.\n\n" +
                    "Permitir este bloqueio?");
            }

            await SendJsonAsync(socket, state.SendGate, new { type = "input_lock_response", requestId, allow });
            if (allow) await SetInputLock(true, socket, state);
            state.Ui?.UpdatePermissions(state);
        }

        static async Task HandleShellRequest(ClientWebSocket socket, AgentState state, JsonElement root)
        {
            var requestId = GetString(root, "requestId");
            var requester = GetString(root, "requester", "Operador remoto");
            var command = GetString(root, "cmd");

            if (!state.AccessActive || !state.ControlActive || string.IsNullOrWhiteSpace(command))
            {
                await SendJsonAsync(socket, state.SendGate, new { type = "shell_denied", requestId });
                return;
            }

            bool allow = await AskAsync(state,
                "Executar comando remoto",
                $"{requester} solicitou executar o comando abaixo:\n\n{command}\n\n" +
                "O comando só será executado se você aprovar esta solicitação específica.\n\n" +
                "Executar agora?");

            if (!allow)
            {
                await SendJsonAsync(socket, state.SendGate, new { type = "shell_denied", requestId });
                state.Ui?.SetActivity("Comando remoto negado.");
                return;
            }

            state.Ui?.SetActivity($"Executando comando aprovado: {Short(command, 80)}");
            string output = await ExecuteShellApproved(command);
            await SendJsonAsync(socket, state.SendGate, new { type = "shell_result", requestId, output });
            state.Ui?.SetActivity("Comando aprovado concluído.");
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
                    CreateNoWindow = false,
                    WindowStyle = ProcessWindowStyle.Normal
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

        static async Task SetInputLock(bool lockStatus, ClientWebSocket socket, AgentState state)
        {
            try
            {
                if (!lockStatus)
                {
                    BlockInput(false);
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

                bool blocked = BlockInput(true);
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
                                BlockInput(false);
                                state.InputLocked = false;
                                state.LastBlockTime = null;
                                var current = state.Socket;
                                if (current?.State == WebSocketState.Open)
                                {
                                    await SendJsonAsync(current, state.SendGate, new { type = "lock_timeout" });
                                    await SendJsonAsync(current, state.SendGate, new { type = "lock_ack", status = false });
                                }
                                state.Ui?.SetActivity("Bloqueio local removido automaticamente após 30 segundos.");
                                state.Ui?.UpdatePermissions(state);
                            }
                        }
                        catch { }
                    });
                }
            }
            catch
            {
                try { BlockInput(false); } catch { }
                state.InputLocked = false;
                state.LastBlockTime = null;
            }
        }

        static async Task RevokeAllAsync(AgentState state)
        {
            try { BlockInput(false); } catch { }
            state.AccessActive = false;
            state.StreamRequested = false;
            state.ControlActive = false;
            state.KeylogActive = false;
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
            try { BlockInput(false); } catch { }
            state.ControlActive = false;
            state.InputLocked = false;
            state.LastBlockTime = null;
            state.Ui?.UpdatePermissions(state);
            state.Ui?.SetActivity("Controle remoto revogado localmente.");

            var socket = state.Socket;
            if (socket?.State == WebSocketState.Open)
                await SendJsonAsync(socket, state.SendGate, new { type = "end_control" });
        }

        static async Task RevokeKeylogAsync(AgentState state)
        {
            state.KeylogActive = false;
            state.Ui?.UpdatePermissions(state);
            state.Ui?.SetActivity("Compartilhamento de teclado interrompido localmente.");

            var socket = state.Socket;
            if (socket?.State == WebSocketState.Open)
                await SendJsonAsync(socket, state.SendGate, new { type = "end_keylog" });
        }

        static async Task ForceUnlockAsync(AgentState state)
        {
            try { BlockInput(false); } catch { }
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
            try { BlockInput(false); } catch { }
            state.AccessActive = false;
            state.StreamRequested = false;
            state.ControlActive = false;
            state.KeylogActive = false;
            state.InputLocked = false;
            state.IsReady = false;
            state.LastBlockTime = null;
            state.Ui?.UpdatePermissions(state);
            state.Ui?.SetConnection(false, activity);
        }

        static Task<bool> AskAsync(AgentState state, string title, string message)
        {
            var ui = state.Ui;
            if (ui == null || ui.IsDisposed) return Task.FromResult(false);
            return ui.AskConsentAsync(title, message);
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

                    try
                    {
                        fullGraphics!.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
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
                    if (flags != 0) mouse_event(flags, 0, 0, 0, UIntPtr.Zero);
                }
                else if (kind == "wheel")
                {
                    int delta = ev.GetProperty("delta").GetInt32();
                    mouse_event(0x0800u, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
                }
                else if (kind == "key")
                {
                    var code = ev.GetProperty("code").GetString() ?? "";
                    var down = ev.GetProperty("down").GetBoolean();
                    ushort vk = VkFromCode(code);
                    if (vk != 0) keybd_event((byte)vk, 0, down ? 0u : 0x0002u, UIntPtr.Zero);
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
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KLTOCHIQUE");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "agent-id.txt");
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(existing)) return existing;
            }

            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, id);
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

    internal sealed class ConsentStatusForm : Form
    {
        readonly Label connectionLabel = new();
        readonly Label accessLabel = new();
        readonly Label controlLabel = new();
        readonly Label keylogLabel = new();
        readonly Label lockLabel = new();
        readonly Label activityLabel = new();
        readonly Button revokeControlButton = new();
        readonly Button revokeKeylogButton = new();
        readonly Button unlockButton = new();
        readonly Button revokeAllButton = new();

        public event Action? RevokeAllRequested;
        public event Action? RevokeControlRequested;
        public event Action? RevokeKeylogRequested;
        public event Action? ForceUnlockRequested;

        public ConsentStatusForm()
        {
            Text = "KL TOCHIQUE — Sessão de Suporte";
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
                Text = "KL TOCHIQUE — CONSENTIMENTO ATIVO",
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                ForeColor = Color.FromArgb(84, 255, 160),
                Location = new Point(22, 20)
            };

            connectionLabel.SetBounds(24, 66, 550, 28);
            accessLabel.SetBounds(24, 108, 550, 26);
            controlLabel.SetBounds(24, 140, 550, 26);
            keylogLabel.SetBounds(24, 172, 550, 26);
            lockLabel.SetBounds(24, 204, 550, 26);
            activityLabel.SetBounds(24, 245, 550, 48);
            activityLabel.ForeColor = Color.Gainsboro;

            revokeControlButton.Text = "Revogar controle";
            revokeControlButton.SetBounds(24, 315, 160, 38);
            revokeControlButton.Click += (_, __) => RevokeControlRequested?.Invoke();

            revokeKeylogButton.Text = "Parar teclado";
            revokeKeylogButton.SetBounds(194, 315, 150, 38);
            revokeKeylogButton.Click += (_, __) => RevokeKeylogRequested?.Invoke();

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
                keylogLabel,
                lockLabel,
                activityLabel,
                revokeControlButton,
                revokeKeylogButton,
                unlockButton,
                revokeAllButton
            });

            SetConnection(false, "Inicializando...");
            UpdatePermissions(new Program.AgentState());
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

        public void UpdatePermissions(Program.AgentState state)
        {
            Ui(() =>
            {
                accessLabel.Text = "Visualização da tela: " + (state.AccessActive ? "AUTORIZADA" : "BLOQUEADA");
                controlLabel.Text = "Controle de mouse/teclado: " + (state.ControlActive ? "AUTORIZADO" : "BLOQUEADO");
                keylogLabel.Text = "Compartilhamento de eventos de teclado: " + (state.KeylogActive ? "ATIVO" : "DESATIVADO");
                lockLabel.Text = "Teclado/mouse local: " + (state.InputLocked ? "BLOQUEADO TEMPORARIAMENTE" : "LIVRE");

                accessLabel.ForeColor = state.AccessActive ? Color.LightGreen : Color.LightGray;
                controlLabel.ForeColor = state.ControlActive ? Color.LightCoral : Color.LightGray;
                keylogLabel.ForeColor = state.KeylogActive ? Color.Gold : Color.LightGray;
                lockLabel.ForeColor = state.InputLocked ? Color.OrangeRed : Color.LightGray;

                revokeControlButton.Enabled = state.ControlActive;
                revokeKeylogButton.Enabled = state.KeylogActive;
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

    internal static class KeyboardHook
    {
        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        static IntPtr hook = IntPtr.Zero;
        static LowLevelKeyboardProc? proc;
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

        static IntPtr SetHook(LowLevelKeyboardProc p)
        {
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            return SetWindowsHookEx(WH_KEYBOARD_LL, p, GetModuleHandle(module?.ModuleName), 0);
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

            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        public static void Uninstall()
        {
            if (hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        [DllImport("user32.dll")]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
