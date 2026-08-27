using System.Drawing.Imaging;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
    readonly Label status = new()
    {
        AutoSize = true,
        Text = "Conectando...",
        Left = 18,
        Top = 18
    };

    readonly Button endButton = new()
    {
        Text = "Encerrar compartilhamento",
        Left = 18,
        Top = 52,
        Width = 210,
        Enabled = false
    };

    ClientWebSocket? socket;
    CancellationTokenSource? lifetime;
    volatile bool accessActive;
    volatile bool streamRequested;

    readonly string agentId;

    public MainForm()
    {
        Text = "EmpresaMonitor";
        Width = 360;
        Height = 150;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        Controls.Add(status);
        Controls.Add(endButton);

        agentId = LoadOrCreateId();

        Shown += async (_, _) => await StartAsync();
        FormClosing += (_, _) => lifetime?.Cancel();

        endButton.Click += async (_, _) =>
        {
            accessActive = false;
            streamRequested = false;
            endButton.Enabled = false;
            status.Text = "Conectado — sem compartilhamento";
            await SendJsonAsync(new { type = "end_access" });
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

                await socket.ConnectAsync(
                    new Uri(BuildConfig.RealtimeUrl),
                    lifetime.Token
                );

                await SendJsonAsync(new
                {
                    type = "agent_hello",
                    key = BuildConfig.AgentKey,
                    id = agentId,
                    name = Environment.MachineName,
                    user = Environment.UserName
                });

                BeginInvoke(() => status.Text = "Conectado — aguardando solicitação");

                await ReceiveLoop(lifetime.Token);
            }
            catch
            {
                if (!lifetime.IsCancellationRequested)
                {
                    BeginInvoke(() => status.Text = "Desconectado — tentando novamente...");
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
                result = await socket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            var text = Encoding.UTF8.GetString(ms.ToArray());

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp))
                continue;

            var type = typeProp.GetString();

            if (type == "access_request")
            {
                BeginInvoke(async () =>
                {
                    var answer = MessageBox.Show(
                        this,
                        "O administrador solicitou acesso para VISUALIZAR sua tela AO VIVO.\n\nVocê autoriza esta sessão?",
                        "EmpresaMonitor — Solicitação de acesso",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information
                    );

                    accessActive = answer == DialogResult.Yes;

                    if (accessActive)
                    {
                        status.Text = "🟢 Compartilhamento autorizado";
                        endButton.Enabled = true;
                    }
                    else
                    {
                        status.Text = "Conectado — acesso recusado";
                        endButton.Enabled = false;
                    }

                    await SendJsonAsync(new
                    {
                        type = "access_response",
                        allow = accessActive
                    });
                });
            }
            else if (type == "stream_start")
            {
                if (accessActive)
                {
                    streamRequested = true;
                    BeginInvoke(() => status.Text = "🔴 Compartilhando tela AO VIVO");
                }
            }
            else if (type == "stream_stop")
            {
                streamRequested = false;
                BeginInvoke(() =>
                {
                    status.Text = accessActive
                        ? "🟢 Acesso autorizado — aguardando visualização"
                        : "Conectado — sem compartilhamento";
                });
            }
            else if (type == "access_ended")
            {
                accessActive = false;
                streamRequested = false;

                BeginInvoke(() =>
                {
                    status.Text = "Conectado — compartilhamento encerrado";
                    endButton.Enabled = false;
                });
            }
        }
    }

    async Task CaptureLoop(CancellationToken ct)
    {
        const int targetWidth = 1280;
        const int targetFps = 12;
        const long jpegQuality = 68L;
        int delayMs = 1000 / targetFps;

        var jpegCodec = ImageCodecInfo
            .GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality,
            jpegQuality
        );

        while (!ct.IsCancellationRequested)
        {
            if (!accessActive || !streamRequested || socket?.State != WebSocketState.Open)
            {
                try { await Task.Delay(120, ct); } catch { }
                continue;
            }

            var started = Environment.TickCount64;

            try
            {
                var bounds = Screen.PrimaryScreen!.Bounds;

                using var full = new Bitmap(
                    bounds.Width,
                    bounds.Height,
                    PixelFormat.Format24bppRgb
                );

                using (var g = Graphics.FromImage(full))
                {
                    g.CopyFromScreen(
                        bounds.Location,
                        Point.Empty,
                        bounds.Size,
                        CopyPixelOperation.SourceCopy
                    );
                }

                int outW = Math.Min(targetWidth, bounds.Width);
                int outH = (int)Math.Round(bounds.Height * (outW / (double)bounds.Width));

                using var scaled = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);

                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode =
                        System.Drawing.Drawing2D.InterpolationMode.Bilinear;

                    g.DrawImage(
                        full,
                        new Rectangle(0, 0, outW, outH),
                        0,
                        0,
                        full.Width,
                        full.Height,
                        GraphicsUnit.Pixel
                    );
                }

                using var ms = new MemoryStream(512 * 1024);
                scaled.Save(ms, jpegCodec, encoderParams);

                var bytes = ms.ToArray();

                if (socket?.State == WebSocketState.Open)
                {
                    await socket.SendAsync(
                        bytes,
                        WebSocketMessageType.Binary,
                        true,
                        ct
                    );
                }
            }
            catch
            {
                // A reconexão do socket é tratada no loop principal.
            }

            var elapsed = (int)(Environment.TickCount64 - started);
            var wait = Math.Max(1, delayMs - elapsed);

            try { await Task.Delay(wait, ct); } catch { }
        }
    }

    async Task SendJsonAsync(object obj)
    {
        if (socket?.State != WebSocketState.Open)
            return;

        var json = JsonSerializer.Serialize(obj);
        var data = Encoding.UTF8.GetBytes(json);

        await socket.SendAsync(
            data,
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        );
    }

    static string LoadOrCreateId()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmpresaMonitor"
        );

        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "agent-id.txt");

        if (File.Exists(path))
        {
            var old = File.ReadAllText(path).Trim();
            if (!string.IsNullOrWhiteSpace(old))
                return old;
        }

        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(path, id);
        return id;
    }
}
