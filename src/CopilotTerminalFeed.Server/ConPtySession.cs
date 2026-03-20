using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CopilotTerminalFeed.Server;

/// <summary>
/// Manages a ConPTY (Windows Pseudo Console) session, providing read/write
/// streams for terminal I/O. This enables full terminal emulation including
/// ANSI escape sequences, colors, and interactive CLI tools like Claude Code.
/// </summary>
public sealed class ConPtySession : IDisposable
{
    private readonly string _command;
    private IntPtr _pseudoConsoleHandle;
    private SafeFileHandle? _pipeInRead;
    private SafeFileHandle? _pipeInWrite;
    private SafeFileHandle? _pipeOutRead;
    private SafeFileHandle? _pipeOutWrite;
    private SafeProcessHandle? _processHandle;
    private Stream? _readStream;
    private Stream? _writeStream;
    private Task? _readTask;
    private bool _disposed;

    /// <summary>Ring buffer of recent output for replay on reconnect.</summary>
    private readonly OutputRingBuffer _outputBuffer = new(capacity: 64 * 1024);

    public event Action<byte[]>? OutputReceived;
    public event Action<int>? ProcessExited;
    public bool IsRunning { get; private set; }
    public int? ExitCode { get; private set; }

    public ConPtySession(string command = "cmd.exe")
    {
        _command = command;
    }

    /// <summary>
    /// Creates the pseudo console, pipes, and spawns the child process.
    /// </summary>
    public void Start(short columns = 120, short rows = 30)
    {
        // Create pipes for the pseudo console
        CreatePipes();

        // Create the pseudo console
        var size = new COORD { X = columns, Y = rows };
        var hr = CreatePseudoConsole(size, _pipeInRead!.DangerousGetHandle(), _pipeOutWrite!.DangerousGetHandle(), 0, out _pseudoConsoleHandle);
        if (hr != 0)
            throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");

        // Spawn the child process attached to the pseudo console
        SpawnProcess();

        // Set up the streams for reading/writing
        _readStream = new FileStream(_pipeOutRead!, FileAccess.Read, 4096, false);
        _writeStream = new FileStream(_pipeInWrite!, FileAccess.Write, 4096, false);

        IsRunning = true;

        // Start reading output in a tracked background task
        _readTask = Task.Run(ReadOutputLoop);
    }

    /// <summary>
    /// Writes data to the terminal's input stream (keyboard input).
    /// </summary>
    public async Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        if (_writeStream is null || _disposed) return;
        await _writeStream.WriteAsync(data, ct);
        await _writeStream.FlushAsync(ct);
    }

    /// <summary>
    /// Resizes the pseudo console.
    /// </summary>
    public void Resize(short columns, short rows)
    {
        if (_pseudoConsoleHandle == IntPtr.Zero) return;
        var size = new COORD { X = columns, Y = rows };
        ResizePseudoConsole(_pseudoConsoleHandle, size);
    }

    /// <summary>
    /// Returns recent terminal output for replay when a new client connects.
    /// </summary>
    public byte[] GetReplayBuffer() => _outputBuffer.ToArray();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsRunning = false;

        _readStream?.Dispose();
        _writeStream?.Dispose();

        if (_pseudoConsoleHandle != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudoConsoleHandle);
            _pseudoConsoleHandle = IntPtr.Zero;
        }

        _processHandle?.Dispose();
        _pipeInRead?.Dispose();
        _pipeInWrite?.Dispose();
        _pipeOutRead?.Dispose();
        _pipeOutWrite?.Dispose();
    }

    private void CreatePipes()
    {
        if (!CreatePipe(out _pipeInRead, out _pipeInWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException("Failed to create input pipe");

        if (!CreatePipe(out _pipeOutRead, out _pipeOutWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException("Failed to create output pipe");
    }

    private void SpawnProcess()
    {
        var startupInfo = new STARTUPINFOEX();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        // Initialize the proc thread attribute list with the pseudo console
        IntPtr attrList = IntPtr.Zero;
        var attrSize = IntPtr.Zero;

        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        attrList = Marshal.AllocHGlobal(attrSize.ToInt32());

        if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize))
            throw new InvalidOperationException("InitializeProcThreadAttributeList failed");

        if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            _pseudoConsoleHandle, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("UpdateProcThreadAttribute failed");

        startupInfo.lpAttributeList = attrList;

        var processInfo = new PROCESS_INFORMATION();

        if (!CreateProcess(null, _command, IntPtr.Zero, IntPtr.Zero, false,
            EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, null, ref startupInfo, out processInfo))
        {
            throw new InvalidOperationException($"CreateProcess failed for '{_command}': {Marshal.GetLastWin32Error()}");
        }

        _processHandle = new SafeProcessHandle(processInfo.hProcess, true);
        CloseHandle(processInfo.hThread);

        DeleteProcThreadAttributeList(attrList);
        Marshal.FreeHGlobal(attrList);
    }

    private async Task ReadOutputLoop()
    {
        var buffer = new byte[4096];
        int exitCode = -1;

        try
        {
            while (!_disposed && _readStream is not null)
            {
                var bytesRead = await _readStream.ReadAsync(buffer);
                if (bytesRead == 0) break;

                var data = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, data, 0, bytesRead);

                _outputBuffer.Write(data);
                OutputReceived?.Invoke(data);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            IsRunning = false;

            // Try to get the process exit code
            try
            {
                if (_processHandle is not null && !_processHandle.IsInvalid &&
                    GetExitCodeProcess(_processHandle.DangerousGetHandle(), out var code))
                {
                    exitCode = (int)code;
                }
            }
            catch { }

            ExitCode = exitCode;
            ProcessExited?.Invoke(exitCode);
        }
    }

    #region Native P/Invoke

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    #endregion
}

/// <summary>
/// Thread-safe ring buffer for terminal output. Stores the last N bytes
/// of output so new WebSocket connections can replay recent content.
/// </summary>
internal sealed class OutputRingBuffer
{
    private readonly byte[] _buffer;
    private int _head;
    private int _count;
    private readonly object _lock = new();

    public OutputRingBuffer(int capacity)
    {
        _buffer = new byte[capacity];
    }

    public void Write(byte[] data)
    {
        lock (_lock)
        {
            foreach (var b in data)
            {
                _buffer[_head] = b;
                _head = (_head + 1) % _buffer.Length;
                if (_count < _buffer.Length) _count++;
            }
        }
    }

    public byte[] ToArray()
    {
        lock (_lock)
        {
            if (_count == 0) return Array.Empty<byte>();

            var result = new byte[_count];
            var start = (_head - _count + _buffer.Length) % _buffer.Length;

            if (start + _count <= _buffer.Length)
            {
                Buffer.BlockCopy(_buffer, start, result, 0, _count);
            }
            else
            {
                var firstPart = _buffer.Length - start;
                Buffer.BlockCopy(_buffer, start, result, 0, firstPart);
                Buffer.BlockCopy(_buffer, 0, result, firstPart, _count - firstPart);
            }

            return result;
        }
    }
}
