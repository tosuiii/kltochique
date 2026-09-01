using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace EmpresaMonitor.Agent
{
    // ---------------------------------------------------------------------------
    // DesktopDuplication — captura via DXGI Desktop Duplication (DDA).
    //
    // Por que existe: o overlay fullscreen é marcado com WDA_EXCLUDEFROMCAPTURE.
    // A captura GDI (CopyFromScreen/BitBlt) NÃO remove a janela da imagem —
    // devolve a caixa preta do overlay. A DDA honra o flag: a janela marcada
    // simplesmente não aparece no frame, que mostra o desktop real por baixo.
    // Assim o operador continua vendo e controlando a tela enquanto o overlay
    // fica fixo para o usuário local (zero flicker, zero tela preta).
    //
    // Limitações (todas tratadas com fallback GDI no CaptureLoop):
    //   - RDP: DuplicateOutput falha (DXGI_ERROR_UNSUPPORTED) → fallback GDI.
    //   - Saída HDR: o frame vem R16G16B16A16_FLOAT → recusamos e caímos p/ GDI.
    //   - Rotação de tela: só suportamos Identity → recusamos e caímos p/ GDI.
    //   - Sem GPU/DXGI 1.2 → TryCreate retorna null → fallback GDI.
    //
    // Ciclo de vida: criado sob demanda (lazy) quando o overlay liga; descartado
    // quando o overlay desliga, quando a resolução muda ou em falha de ACCESS_LOST.
    // ---------------------------------------------------------------------------
    internal sealed class DesktopDuplication : IDisposable
    {
        ID3D11Device? device;
        ID3D11DeviceContext? context;
        IDXGIOutputDuplication? duplication;
        ID3D11Texture2D? staging;
        Bitmap? frameBmp;
        readonly int frameW;
        readonly int frameH;
        bool disposed;

        public int SourceWidth => frameW;
        public int SourceHeight => frameH;

        DesktopDuplication(ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication duplication)
        {
            this.device = device;
            this.context = context;
            this.duplication = duplication;

            var desc = duplication.Description;
            frameW = (int)desc.ModeDescription.Width;
            frameH = (int)desc.ModeDescription.Height;

            var texDesc = new Texture2DDescription
            {
                Width = desc.ModeDescription.Width,
                Height = desc.ModeDescription.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.ModeDescription.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };
            staging = device.CreateTexture2D(texDesc);
        }

        // Cria a duplicação para a saída que cobre o monitor primário. Retorna
        // null (nunca lança) quando DDA não está disponível — o chamador faz
        // fallback para a via GDI.
        public static DesktopDuplication? TryCreate(Rectangle primaryBounds)
        {
            IDXGIAdapter1? adapter = null;
            IDXGIOutput? output = null;
            try
            {
                using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                if (!TryFindOutput(factory, primaryBounds, out adapter, out output))
                    return null;

                ID3D11Device? dev = null;
                ID3D11DeviceContext? ctx = null;
                IDXGIOutputDuplication? dup = null;
                try
                {
                    D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None,
                        new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
                        out dev, out ctx).CheckError();

                    // IDXGIOutput5 existe desde o Win10 1703; em builds mais antigos
                    // cai para IDXGIOutput1 (mesma duplicação, sem mudança de API).
                    var o5 = output.QueryInterfaceOrNull<IDXGIOutput5>();
                    try
                    {
                        if (o5 != null) dup = o5.DuplicateOutput(dev);
                        else dup = output.QueryInterfaceOrNull<IDXGIOutput1>()?.DuplicateOutput(dev);
                    }
                    finally { o5?.Dispose(); }

                    if (dup == null) return null;

                    var ddesc = dup.Description;
                    // HDR: o frame não é 8-bit BGRA → recusa (fallback GDI lida).
                    if (ddesc.ModeDescription.Format != Format.B8G8R8A8_UNorm) return null;
                    // Rotação diferente de Identity exigiria correção geométrica → recusa.
                    if (ddesc.Rotation != ModeRotation.Identity) return null;

                    var self = new DesktopDuplication(dev, ctx, dup);
                    dev = null; ctx = null; dup = null;
                    return self;
                }
                finally
                {
                    dup?.Dispose();
                    ctx?.Dispose();
                    dev?.Dispose();
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                adapter?.Dispose();
                output?.Dispose();
            }
        }

        static bool TryFindOutput(IDXGIFactory1 factory, Rectangle primary, out IDXGIAdapter1? matchAdapter, out IDXGIOutput? matchOutput)
        {
            matchAdapter = null;
            matchOutput = null;
            int ai = 0;
            while (factory.EnumAdapters1((uint)ai++, out IDXGIAdapter1 adapter).Success)
            {
                int oi = 0;
                while (adapter.EnumOutputs((uint)oi++, out IDXGIOutput output).Success)
                {
                    var dc = output.Description.DesktopCoordinates;
                    bool isMatch = matchOutput == null ||
                        (dc.Left == primary.Left && dc.Top == primary.Top &&
                         dc.Right - dc.Left == primary.Width && dc.Bottom - dc.Top == primary.Height);
                    if (isMatch)
                    {
                        matchAdapter?.Dispose();
                        matchOutput?.Dispose();
                        matchAdapter = adapter;
                        matchOutput = output;
                    }
                    else
                    {
                        output.Dispose();
                    }
                }
                if (adapter != matchAdapter) adapter.Dispose();
            }
            return matchAdapter != null && matchOutput != null;
        }

        // Captura o desktop (sem janelas WDA_EXCLUDEFROMCAPTURE) e desenha em
        // dest. Retorna true quando um frame válido (novo ou reutilizado) foi
        // desenhado; false quando DDA falhou e o chamador deve usar GDI.
        public bool CaptureFrame(Graphics dest, Size destSize)
        {
            if (disposed || duplication == null || staging == null || context == null) return false;
            bool frameAcquired = false;
            try
            {
                // Primeiro frame: espera um pouco (screen pode estar estático).
                // Depois: timeout 0 — se não houver frame novo, reutiliza o atual.
                int timeout = frameBmp == null ? 250 : 0;
                var result = duplication.AcquireNextFrame((uint)timeout, out OutduplFrameInfo info, out IDXGIResource resource);
                if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    if (frameBmp == null) return false; // ainda sem nenhum frame → fallback GDI
                    DrawCurrent(dest, destSize);
                    return true;
                }
                if (result.Failure) return false; // ACCESS_LOST/UNSUPPORTED → recriar/fallback
                frameAcquired = true;

                try
                {
                    using var tex = resource.QueryInterface<ID3D11Texture2D>();
                    context.CopyResource(staging, tex);
                }
                finally { resource.Dispose(); }

                context.Map(staging, 0u, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped);
                try
                {
                    int w = frameW, h = frameH;
                    int rowBytes = w * 4;
                    if (frameBmp == null || frameBmp.Width != w || frameBmp.Height != h)
                    {
                        frameBmp?.Dispose();
                        frameBmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                    }
                    var data = frameBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        var buf = new byte[rowBytes];
                        for (int y = 0; y < h; y++)
                        {
                            Marshal.Copy(IntPtr.Add(mapped.DataPointer, y * (int)mapped.RowPitch), buf, 0, rowBytes);
                            Marshal.Copy(buf, 0, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
                        }
                    }
                    finally { frameBmp.UnlockBits(data); }
                }
                finally { context.Unmap(staging, 0u); }

                duplication.ReleaseFrame();
                frameAcquired = false;

                DrawCurrent(dest, destSize);
                return true;
            }
            catch
            {
                if (frameAcquired)
                {
                    try { duplication?.ReleaseFrame(); } catch { }
                }
                return false;
            }
        }

        void DrawCurrent(Graphics dest, Size destSize)
        {
            if (frameBmp == null) return;
            dest.DrawImage(frameBmp, new Rectangle(0, 0, destSize.Width, destSize.Height),
                new Rectangle(0, 0, frameW, frameH), GraphicsUnit.Pixel);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            frameBmp?.Dispose();
            try { duplication?.ReleaseFrame(); } catch { }
            staging?.Dispose();
            duplication?.Dispose();
            context?.Dispose();
            device?.Dispose();
        }
    }
}
