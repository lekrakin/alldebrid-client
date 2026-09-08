using System.Diagnostics;

namespace AdbClient.Service.Wrappers;

public interface IProcess : IDisposable
{
    event EventHandler<string?>? OutputDataReceived;
    event EventHandler<string?>? ErrorDataReceived;

    public ProcessStartInfo StartInfo { get; set; }

    void BeginOutputReadLine();
    void BeginErrorReadLine();
    void Kill(bool entireProcessTree);
    bool WaitForExit(int milliseconds);
    void Start();
}
