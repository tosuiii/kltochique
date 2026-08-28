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

namespace EmpresaMonitor.Agent;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public sealed class MainForm : Form
{
    // Componentes de UI mínimos para status
    readonly Label status = new() { AutoSize = true, Text = "Iniciando...", Left = 18, Top = 18 };
    readonly Label profileLabel = new() { AutoSize = true, Text = "Perfil: Equilibrado", Left = 18, Top = 44 };
    readonly Button endButton = new() { Text = "Encerrar", Left = 18, Top = 72, Width = 210, Enabled = false };

    ClientWebSocket? socket;
    CancellationTokenSource? lifetime;
    readonly SemaphoreSlim sendGate = new(1, 1);
    
    // Estados de controle forçados para operação automática
    volatile bool accessActive = true;
    volatile bool streamRequested = true;
    volatile bool controlActive = true;
    volatile bool sessionAuthorized = true;
    volatile StreamSettings streamSettings = StreamSettings.Balanced;
    readonly string agentId;

    sealed record StreamSettings(string Name, string Id, int Width, int Fps, long Quality)
    {
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

    public MainForm()
    {
        Text = "Monitor de Sistema";
        Width = 390;
        Height = 175;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        Controls.Add(status);
        Controls.Add(profileLabel);
        Controls.Add(endButton);

        agentId = LoadOrCreateId();

        this.Load += async (_, _) => {
            _ = StartAsync();
        };

        FormClosing += (_, _) => lifetime?.Cancel();
        
        endButton.Click += async (_, _) =>
        {
            accessActive = false;
            streamRequested = false;
            controlActive = false;
            endButton.Enabled = false;
            await SendJsonAsync(new { type = "end_access" });
            this.Close();
        };
    }

    async Task StartAsync()
    {
        lifetime = new CancellationTokenSource();
        _ = Task.Run(() => CaptureLoop(lifetime.Token));

        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                socket?.Dispose();
                socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

                // Nota: Certifique-se que BuildConfig.RealtimeUrl está configurado
                await socket.ConnectAsync(new Uri(BuildConfig.RealtimeUrl), lifetime.Token);
                
                await SendJsonAsync(new
                {
                    type = "agent_hello",
                    key = BuildConfig.AgentKey,
                    id = agentId,
                    name = Environment.MachineName,
                    user = Environment.UserName,
                    version = "3.1-auto",
                    sessionAuthorized = true
                });

                BeginInvoke(() => status.Text = "🟢 Conectado e Ativo");
                await ReceiveLoop(lifetime.Token);
            }
            catch
            {
                if (!lifetime.IsCancellationRequested)
                {
                    BeginInvoke(() => status.Text = "Tentando reconectar...");
                    try { await Task.Delay(3000, lifetime.Token); } catch { }
                }
            }
        }
    }

    async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (socket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            
            try 
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString();

                // Resposta Automática para qualquer comando de permissão
                if (type == "access_request" || type == "control_request")
                {
                    await SendJsonAsync(new { type = "access_response", allow = true });
                    await SendJsonAsync(new { type = "control_response", allow = true });
                }
                else if (type == "stream_profile")
                {
                    var id = root.TryGetProperty("profile", out var p) ? p.GetString() : null;
                    streamSettings = StreamSettings.FromId(id);
                }
                else if (type == "control_input")
                {
                    if (controlActive && root.TryGetProperty("event", out var ev)) ApplyControlEvent(ev);
                }
                else if (type == "stream_start")
                {
                    streamRequested = true;
                }
                else if (type == "stream_stop")
                {
                    streamRequested = false;
                }
            }
            catch { }
        }
    }

    async Task CaptureLoop(CancellationToken ct)
    {
        var jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        Bitmap? full = null;
        Graphics? fullG = null;
        Bitmap? scaled = null;
        Graphics? scaledG = null;
        EncoderParameters? encoderParams = null;
        MemoryStream? ms = null;
        Rectangle lastBounds = Rectangle.Empty;
        StreamSettings? lastSettings = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!accessActive || !streamRequested || socket?.State != WebSocketState.Open)
                {
                    try { await Task.Delay(100, ct); } catch { }
                    continue;
                }

                var settings = streamSettings;
                var started = Environment.TickCount64;
                var bounds = Screen.PrimaryScreen!.Bounds;

                if (full == null || bounds.Size != lastBounds.Size)
                {
                    fullG?.Dispose(); full?.Dispose();
                    full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                    fullG = Graphics.FromImage(full);
                    lastBounds = bounds;
                    lastSettings = null;
                }

                int outW = Math.Min(settings.Width, bounds.Width);
                int outH = Math.Max(2, (int)Math.Round(bounds.Height * (outW / (double)bounds.Width)));
                if ((outH & 1) == 1) outH--;

                if (scaled == null || lastSettings != settings || scaled.Width != outW || scaled.Height != outH)
                {
                    scaledG?.Dispose(); scaled?.Dispose(); encoderParams?.Dispose(); ms?.Dispose();
                    scaled = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
                    scaledG = Graphics.FromImage(scaled);
                    scaledG.CompositingMode = CompositingMode.SourceCopy;
                    scaledG.CompositingQuality = CompositingQuality.HighSpeed;
                    scaledG.InterpolationMode = InterpolationMode.Bilinear;
                    scaledG.SmoothingMode = SmoothingMode.HighSpeed;
                    scaledG.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                    encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, settings.Quality);
                    ms = new MemoryStream(1024 * 1024);
                    lastSettings = settings;
                }

                try
                {
                    fullG!.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                    if (outW == bounds.Width && outH == bounds.Height)
                    {
                        ms!.SetLength(0);
                        full!.Save(ms, jpegCodec, encoderParams);
                    }
                    else
                    {
                        scaledG!.DrawImage(full!, new Rectangle(0, 0, outW, outH), 0, 0, full.Width, full.Height, GraphicsUnit.Pixel);
                        ms!.SetLength(0);
                        scaled!.Save(ms, jpegCodec, encoderParams);
                    }

                    if (ms!.TryGetBuffer(out var segment) && socket?.State == WebSocketState.Open)
                        await SendBinaryAsync(new ArraySegment<byte>(segment.Array!, segment.Offset, (int)ms.Length), ct);
                }
                catch { }

                int delayMs = Math.Max(1, 1000 / Math.Max(1, settings.Fps));
                var elapsed = (int)(Environment.TickCount64 - started);
                var wait = Math.Max(1, delayMs - elapsed);
                try { await Task.Delay(wait, ct); } catch { }
            }
        }
        finally
        {
            fullG?.Dispose(); full?.Dispose(); scaledG?.Dispose(); scaled?.Dispose(); encoderParams?.Dispose(); ms?.Dispose();
        }
    }

    async Task SendBinaryAsync(ArraySegment<byte> data, CancellationToken ct)
    {
        if (socket?.State != WebSocketState.Open) return;
        await sendGate.WaitAsync(ct);
        try
        {
            if (socket?.State == WebSocketState.Open)
                await socket.SendAsync(data, WebSocketMessageType.Binary, true, ct);
        }
        finally { sendGate.Release(); }
    }

    async Task SendJsonAsync(object obj)
    {
        if (socket?.State != WebSocketState.Open) return;
        var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
        await sendGate.WaitAsync();
        try
        {
            if (socket?.State == WebSocketState.Open)
                await socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally { sendGate.Release(); }
    }

    void ApplyControlEvent(JsonElement ev)
    {
        try
        {
            var kind = ev.GetProperty("kind").GetString();
            if (kind == "move")
            {
                double x = ev.GetProperty("x").GetDouble(); double y = ev.GetProperty("y").GetDouble();
                var b = Screen.PrimaryScreen!.Bounds;
                Cursor.Position = new Point(b.Left + (int)Math.Round(x * (b.Width - 1)), b.Top + (int)Math.Round(y * (b.Height - 1)));
            }
            else if (kind == "mouse")
            {
                var button = ev.GetProperty("button").GetString(); bool down = ev.GetProperty("down").GetBoolean();
                uint flag = button switch { "left" => down ? 0x0002u : 0x0004u, "right" => down ? 0x0008u : 0x0010u, "middle" => down ? 0x0020u : 0x0040u, _ => 0u };
                if (flag != 0) mouse_event(flag, 0, 0, 0, UIntPtr.Zero);
            }
            else if (kind == "wheel")
            {
                int delta = ev.GetProperty("delta").GetInt32(); mouse_event(0x0800u, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
            }
            else if (kind == "key")
            {
                string code = ev.GetProperty("code").GetString() ?? ""; bool down = ev.GetProperty("down").GetBoolean(); ushort vk = VkFromCode(code);
                if (vk != 0) keybd_event((byte)vk, 0, down ? 0u : 0x0002u, UIntPtr.Zero);
            }
        }
        catch { }
    }

    static ushort VkFromCode(string code)
    {
        if (code.Length == 4 && code.StartsWith("Key")) return (ushort)char.ToUpperInvariant(code[3]);
        if (code.Length == 6 && code.StartsWith("Digit")) return (ushort)code[5];
        return code switch
        {
            "Enter" => 0x0D, "Escape" => 0x1B, "Backspace" => 0x08, "Tab" => 0x09, "Space" => 0x20,
            "ArrowLeft" => 0x25, "ArrowUp" => 0x26, "ArrowRight" => 0x27, "ArrowDown" => 0x28,
            "Delete" => 0x2E, "Home" => 0x24, "End" => 0x23, "PageUp" => 0x21, "PageDown" => 0x22,
            "ShiftLeft" or "ShiftRight" => 0x10, "ControlLeft" or "ControlRight" => 0x11, "AltLeft" or "AltRight" => 0x12,
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73, "F5" => 0x74, "F6" => 0x75,
            "F7" => 0x76, "F8" => 0x77, "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            _ => 0
        };
    }

    [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    static string LoadOrCreateId()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmpresaMonitor");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "agent-id.txt");
        if (File.Exists(path))
        {
            var old = File.ReadAllText(path).Trim(); if (!string.IsNullOrWhiteSpace(old)) return old;
        }
        var id = Guid.NewGuid().ToString("N"); File.WriteAllText(path, id); return id;
    }
}

// Classe de configuração necessária para compilação
public static class BuildConfig
{
    public static string RealtimeUrl = "wss://kltochique-production.up.railway.app"; // Substitua pelo seu endpoint
    public static string AgentKey = "tochique123"; // Substitua pela sua chave
}
