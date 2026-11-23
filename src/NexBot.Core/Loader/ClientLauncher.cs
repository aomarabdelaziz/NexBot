using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using static NexBot.Loader.NativeMethods;

namespace NexBot.Loader;

/// <summary>What <see cref="ClientLauncher"/> needs to start the game client.</summary>
/// <param name="ClientDirectory">Folder that contains sro_client.exe and Media.pk2.</param>
/// <param name="Executable">Client executable name, normally sro_client.exe.</param>
/// <param name="Locale">Content ID / locale from DivisionInfo.txt (22 for vSRO).</param>
/// <param name="DivisionIndex">Index of the division in DivisionInfo.txt.</param>
/// <param name="GatewayIndex">Index of the gateway inside the division.</param>
/// <param name="GatewayPort">Real gateway port from Gateport.txt; connections to this port are redirected.</param>
/// <param name="ProxyPort">Local port of the NexBot proxy (127.0.0.1).</param>
public sealed record LaunchOptions(
    string ClientDirectory,
    string Executable,
    byte Locale,
    byte DivisionIndex,
    byte GatewayIndex,
    ushort GatewayPort,
    ushort ProxyPort);

/// <summary>
/// Starts sro_client.exe the way a launcher does and redirects its gateway connection to the local proxy.
/// </summary>
/// <remarks>
/// <para>
/// Same idea as RSBot's <c>ClientManager.Start</c> (client started suspended, code injected, then resumed), but
/// without a native Loader.dll: the redirect is a ~45 byte x86 stub written straight into the client.
/// </para>
/// <list type="number">
///   <item>Create the "Silkroad Online Launcher" and "Ready" mutexes the client checks for.</item>
///   <item>Start the client suspended with the launcher arguments <c>/locale division gateway</c>.</item>
///   <item>Run <c>LoadLibraryA("ws2_32.dll")</c> on a remote thread. That also runs the process initialization, so the
///   client's imports are loaded.</item>
///   <item>Patch the hot-patch prologue (<c>mov edi,edi; push ebp; mov ebp,esp</c>) of <c>ws2_32!connect</c> with a
///   jump to the stub. The stub rewrites an IPv4 <c>sockaddr</c> whose port is the gateway port to
///   127.0.0.1:proxy port, then runs the original prologue and jumps back.</item>
///   <item>Resume the main thread.</item>
/// </list>
/// <para>The bot must run as a 32-bit process, so system DLL addresses match the 32-bit client's.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ClientLauncher
{
    private static readonly byte[] s_hotPatchPrologue = [0x8B, 0xFF, 0x55, 0x8B, 0xEC];
    private static readonly List<IntPtr> s_launcherMutexes = [];
    private static readonly Lock s_mutexLock = new();

    /// <summary>Starts the client. Throws <see cref="ClientLaunchException"/> when any step fails.</summary>
    public static Process Launch(LaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (IntPtr.Size != 4)
            throw new ClientLaunchException("NexBot must run as a 32-bit (x86) process to patch the 32-bit game client.");

        string exePath = Path.Combine(options.ClientDirectory, options.Executable);
        if (!File.Exists(exePath))
            throw new ClientLaunchException($"Client executable not found: {exePath}");

        EnsureLauncherMutexes();

        string args = string.Create(CultureInfo.InvariantCulture,
            $"/{options.Locale} {options.DivisionIndex} {options.GatewayIndex}");
        char[] commandLine = $"\"{exePath}\" {args}\0".ToCharArray(); // CreateProcessW may write to it
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };

        if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED, IntPtr.Zero,
                options.ClientDirectory, ref startup, out var pi))
            throw new ClientLaunchException("CreateProcess failed", new Win32Exception(Marshal.GetLastWin32Error()));

        bool resumed = false;
        try
        {
            IntPtr remoteWs2 = RemoteLoadLibrary(pi.hProcess, "ws2_32.dll");
            InstallConnectRedirect(pi.hProcess, remoteWs2, options.GatewayPort, options.ProxyPort);

            if (ResumeThread(pi.hThread) == uint.MaxValue)
                throw new ClientLaunchException("ResumeThread failed", new Win32Exception(Marshal.GetLastWin32Error()));
            resumed = true;

            return Process.GetProcessById(pi.dwProcessId);
        }
        finally
        {
            if (!resumed)
                TerminateProcess(pi.hProcess, 1);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
    }

    /// <summary>Changes the client's window title.</summary>
    public static void SetTitle(Process process, string title)
    {
        ArgumentNullException.ThrowIfNull(process);
        process.Refresh();
        if (process.MainWindowHandle != IntPtr.Zero)
            SetWindowText(process.MainWindowHandle, title);
    }

    /// <summary>The client windows found so far, by process id (a hidden window is not found by .NET again).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, IntPtr> s_windows = new();

    /// <summary>Shows or hides the client's window. Showing it brings it to the front.</summary>
    public static void SetVisible(Process process, bool visible)
    {
        ArgumentNullException.ThrowIfNull(process);
        IntPtr window = WindowOf(process);
        if (window == IntPtr.Zero)
            return;
        ShowWindow(window, visible ? SW_SHOW : SW_HIDE);
        if (visible)
            SetForegroundWindow(window);
    }

    /// <summary>
    /// The client's main window, also while it is hidden: <see cref="Process.MainWindowHandle"/> only finds visible
    /// windows, so the handle is remembered, and otherwise looked up among all top-level windows of the process
    /// (the one with a title).
    /// </summary>
    private static IntPtr WindowOf(Process process)
    {
        int id = process.Id;
        if (s_windows.TryGetValue(id, out IntPtr known) && IsWindow(known))
            return known;
        process.Refresh();
        IntPtr window = process.MainWindowHandle;
        if (window == IntPtr.Zero)
        {
            for (IntPtr w = GetWindow(GetDesktopWindow(), GW_CHILD); w != IntPtr.Zero; w = GetWindow(w, GW_HWNDNEXT))
            {
                GetWindowThreadProcessId(w, out uint owner);
                if (owner == (uint)id && GetWindowTextLengthW(w) > 0)
                {
                    window = w;
                    break;
                }
            }
        }
        if (window != IntPtr.Zero)
            s_windows[id] = window;
        return window;
    }

    /// <summary>The client refuses to start unless a launcher created these mutexes. They live as long as the bot.</summary>
    private static void EnsureLauncherMutexes()
    {
        lock (s_mutexLock)
        {
            if (s_launcherMutexes.Count > 0)
                return;
            foreach (string name in new[] { "Silkroad Online Launcher", "Ready" })
            {
                IntPtr handle = CreateMutex(IntPtr.Zero, false, name);
                if (handle != IntPtr.Zero)
                    s_launcherMutexes.Add(handle);
            }
        }
    }

    /// <summary>Loads a DLL into the (suspended) process and returns its base address there.</summary>
    private static IntPtr RemoteLoadLibrary(IntPtr process, string dll)
    {
        IntPtr loadLibrary = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryA");
        if (loadLibrary == IntPtr.Zero)
            throw new ClientLaunchException("LoadLibraryA not found in kernel32.dll.");

        byte[] name = Encoding.ASCII.GetBytes(dll + "\0");
        IntPtr remoteName = VirtualAllocEx(process, IntPtr.Zero, (nuint)name.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remoteName == IntPtr.Zero)
            throw new ClientLaunchException("VirtualAllocEx failed", new Win32Exception(Marshal.GetLastWin32Error()));

        try
        {
            Write(process, remoteName, name);

            IntPtr thread = CreateRemoteThread(process, IntPtr.Zero, 0, loadLibrary, remoteName, 0, IntPtr.Zero);
            if (thread == IntPtr.Zero)
                throw new ClientLaunchException("CreateRemoteThread failed", new Win32Exception(Marshal.GetLastWin32Error()));
            try
            {
                if (WaitForSingleObject(thread, 15000) != WAIT_OBJECT_0)
                    throw new ClientLaunchException($"Timed out loading {dll} into the client.");
                if (!GetExitCodeThread(thread, out uint module) || module == 0)
                    throw new ClientLaunchException($"Could not load {dll} into the client.");
                return (IntPtr)(int)module;
            }
            finally
            {
                CloseHandle(thread);
            }
        }
        finally
        {
            VirtualFreeEx(process, remoteName, 0, MEM_RELEASE);
        }
    }

    private static void InstallConnectRedirect(IntPtr process, IntPtr remoteWs2, ushort gatewayPort, ushort proxyPort)
    {
        IntPtr localWs2 = LoadLibrary("ws2_32.dll");
        IntPtr localConnect = GetProcAddress(localWs2, "connect");
        if (localWs2 == IntPtr.Zero || localConnect == IntPtr.Zero)
            throw new ClientLaunchException("ws2_32!connect not found.");

        // Same DLL, same offset: the remote address does not depend on where ws2_32 is mapped.
        int connect = remoteWs2.ToInt32() + (localConnect.ToInt32() - localWs2.ToInt32());

        byte[] prologue = new byte[5];
        if (!ReadProcessMemory(process, connect, prologue, 5, out _))
            throw new ClientLaunchException("Could not read ws2_32!connect in the client.", new Win32Exception(Marshal.GetLastWin32Error()));
        if (!prologue.AsSpan().SequenceEqual(s_hotPatchPrologue))
            throw new ClientLaunchException(
                $"ws2_32!connect has an unexpected prologue ({Convert.ToHexString(prologue)}); it is already hooked or this Windows version is not supported.");

        IntPtr stub = VirtualAllocEx(process, IntPtr.Zero, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (stub == IntPtr.Zero)
            throw new ClientLaunchException("VirtualAllocEx failed", new Win32Exception(Marshal.GetLastWin32Error()));

        byte[] code = ConnectRedirectStub.Build(stub.ToInt32(), connect, gatewayPort, proxyPort);
        Write(process, stub, code);
        FlushInstructionCache(process, stub, (nuint)code.Length);

        byte[] jump = ConnectRedirectStub.Jump(connect, stub.ToInt32());
        if (!VirtualProtectEx(process, connect, 5, PAGE_EXECUTE_READWRITE, out uint oldProtect))
            throw new ClientLaunchException("VirtualProtectEx failed", new Win32Exception(Marshal.GetLastWin32Error()));
        Write(process, connect, jump);
        VirtualProtectEx(process, connect, 5, oldProtect, out _);
        FlushInstructionCache(process, connect, 5);
    }

    private static void Write(IntPtr process, IntPtr address, byte[] data)
    {
        if (!WriteProcessMemory(process, address, data, (nuint)data.Length, out nuint written) || written != (nuint)data.Length)
            throw new ClientLaunchException("WriteProcessMemory failed", new Win32Exception(Marshal.GetLastWin32Error()));
    }
}

/// <summary>Builds the x86 machine code of the <c>connect</c> redirect.</summary>
public static class ConnectRedirectStub
{
    /// <summary>Offset of the original prologue inside the stub (where every "no match" branch jumps).</summary>
    private const int OriginalOffset = 35;

    /// <summary>
    /// <code>
    ///   mov  eax, [esp+8]              ; const sockaddr* name
    ///   test eax, eax
    ///   jz   original
    ///   cmp  word [eax], 2             ; AF_INET
    ///   jne  original
    ///   cmp  word [eax+2], gatewayPort ; network byte order
    ///   jne  original
    ///   mov  word [eax+2], proxyPort
    ///   mov  dword [eax+4], 127.0.0.1
    /// original:
    ///   mov  edi, edi
    ///   push ebp
    ///   mov  ebp, esp
    ///   jmp  connect+5
    /// </code>
    /// </summary>
    public static byte[] Build(int stubAddress, int connectAddress, ushort gatewayPort, ushort proxyPort)
    {
        var code = new List<byte>(48);
        code.AddRange([0x8B, 0x44, 0x24, 0x08]);                         // mov eax,[esp+8]
        code.AddRange([0x85, 0xC0]);                                     // test eax,eax
        code.AddRange([0x74, (byte)(OriginalOffset - 8)]);              // jz original
        code.AddRange([0x66, 0x83, 0x38, 0x02]);                         // cmp word [eax],2
        code.AddRange([0x75, (byte)(OriginalOffset - 14)]);             // jne original
        code.AddRange([0x66, 0x81, 0x78, 0x02, (byte)(gatewayPort >> 8), (byte)gatewayPort]); // cmp word [eax+2],port
        code.AddRange([0x75, (byte)(OriginalOffset - 22)]);             // jne original
        code.AddRange([0x66, 0xC7, 0x40, 0x02, (byte)(proxyPort >> 8), (byte)proxyPort]);     // mov word [eax+2],proxy
        code.AddRange([0xC7, 0x40, 0x04, 0x7F, 0x00, 0x00, 0x01]);       // mov dword [eax+4],127.0.0.1
        if (code.Count != OriginalOffset)
            throw new InvalidOperationException("Stub layout changed; fix the branch offsets.");
        code.AddRange([0x8B, 0xFF, 0x55, 0x8B, 0xEC]);                   // original prologue
        code.AddRange(Jump(stubAddress + code.Count, connectAddress + 5)); // jmp connect+5
        return [.. code];
    }

    /// <summary>A 5-byte relative <c>jmp</c> placed at <paramref name="from"/>.</summary>
    public static byte[] Jump(int from, int to)
    {
        int rel = to - (from + 5);
        return [0xE9, (byte)rel, (byte)(rel >> 8), (byte)(rel >> 16), (byte)(rel >> 24)];
    }
}

/// <summary>Raised when the client cannot be started or patched.</summary>
public sealed class ClientLaunchException : Exception
{
    public ClientLaunchException() { }
    public ClientLaunchException(string message) : base(message) { }
    public ClientLaunchException(string message, Exception innerException) : base(message, innerException) { }
}
