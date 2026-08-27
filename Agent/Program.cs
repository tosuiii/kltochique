using System.Drawing.Imaging;
using System.Net.Http.Json;
using System.Windows.Forms;

namespace EmpresaMonitor.Agent;

internal static class Program
{
    [STAThread]
    static async Task Main()
    {
        ApplicationConfiguration.Initialize();

        using var http = new HttpClient
        {
            BaseAddress = new Uri(BuildConfig.ServerUrl),
            Timeout = TimeSpan.FromSeconds(20)
        };

        http.DefaultRequestHeaders.Add("X-Agent-Key", BuildConfig.AgentKey);

        try
        {
            var health = await http.GetAsync("/api/health");
            health.EnsureSuccessStatusCode();
        }
        catch
        {
            MessageBox.Show(
                "Não foi possível conectar ao servidor EmpresaMonitor.\n\n" +
                BuildConfig.ServerUrl,
                "EmpresaMonitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            return;
        }

        string id;

        try
        {
            id = await RegisterAsync(http);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Falha ao registrar este computador:\n\n" + ex.Message,
                "EmpresaMonitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            return;
        }

        bool requestAlreadyShown = false;

        while (true)
        {
            try
            {
                await http.PostAsJsonAsync(
                    "/api/heartbeat",
                    new { id, user = Environment.UserName }
                );

                var state = await http.GetFromJsonAsync<AccessState>(
                    $"/api/computers/{id}/access-state"
                );

                if (state?.AccessRequested == true && !requestAlreadyShown)
                {
                    requestAlreadyShown = true;

                    var answer = MessageBox.Show(
                        "O administrador solicitou acesso para VISUALIZAR sua tela.\n\n" +
                        "Você autoriza esta sessão?",
                        "EmpresaMonitor — Solicitação de acesso",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information
                    );

                    await http.PostAsJsonAsync(
                        $"/api/computers/{id}/authorize",
                        new { allow = answer == DialogResult.Yes }
                    );
                }

                if (state?.AccessRequested != true)
                    requestAlreadyShown = false;

                if (state?.AccessActive == true)
                {
                    await SendScreenshotAsync(http, id);
                    await Task.Delay(1200);
                    continue;
                }
            }
            catch
            {
                // MVP: tenta novamente automaticamente.
            }

            await Task.Delay(2500);
        }
    }

    static async Task<string> RegisterAsync(HttpClient http)
    {
        var response = await http.PostAsJsonAsync(
            "/api/register",
            new
            {
                id = "",
                name = Environment.MachineName,
                user = Environment.UserName
            }
        );

        response.EnsureSuccessStatusCode();

        var data = await response.Content.ReadFromJsonAsync<RegisterResponse>();
        return data!.Id;
    }

    static async Task SendScreenshotAsync(HttpClient http, string id)
    {
        var bounds = Screen.PrimaryScreen!.Bounds;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                bounds.Location,
                Point.Empty,
                bounds.Size
            );
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Jpeg);
        stream.Position = 0;

        using var content = new StreamContent(stream);
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

        var response = await http.PostAsync(
            $"/api/computers/{id}/frame",
            content
        );

        response.EnsureSuccessStatusCode();
    }

    record RegisterResponse(string Id);
    record AccessState(bool AccessRequested, bool AccessActive);
}
