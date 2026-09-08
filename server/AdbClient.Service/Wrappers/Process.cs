using System.Diagnostics;

namespace AdbClient.Service.Wrappers;

public class Process : IProcess
{
    private readonly System.Diagnostics.Process _process = new();

    public ProcessStartInfo StartInfo
    {
        get => _process.StartInfo;
        set => _process.StartInfo = value;
    }

    public event EventHandler<string?>? OutputDataReceived;
    public event EventHandler<string?>? ErrorDataReceived;

    public void Dispose()
    {
        _process.Dispose();
        GC.SuppressFinalize(this);
    }

    public void BeginOutputReadLine()
    {
        _process.OutputDataReceived += (sender, args) => OutputDataReceived?.Invoke(sender, args.Data);
        _process.BeginOutputReadLine();
    }

    public void BeginErrorReadLine()
    {
        _process.ErrorDataReceived += (sender, args) => ErrorDataReceived?.Invoke(sender, args.Data);
        _process.BeginErrorReadLine();
    }

    public void Kill(bool entireProcessTree)
    {
        _process.Kill(entireProcessTree);
    }

    public bool WaitForExit(int milliseconds)
    {
        return _process.WaitForExit(milliseconds);
    }

    public void Start()
    {
        _process.Start();
    }
}
