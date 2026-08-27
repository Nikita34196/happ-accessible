using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace HappAccessible.Services;

public sealed class SingleInstanceManager : IDisposable
{
    public const string MutexName = "Global\\HappAccessible.SingleInstance.v1";
    public const string PipeName = "HappAccessible.Activate";
    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;
    private CancellationTokenSource? _listenCts;
    private Task? _listenTask;

    public SingleInstanceManager()
    {
        _ownsMutex = false;
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _ownsMutex);
    }

    public bool IsFirstInstance => _ownsMutex;

    public bool TryActivateExistingInstance(int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("activate");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StartActivationListener(Action onActivate)
    {
        _listenCts = new CancellationTokenSource();
        var ct = _listenCts.Token;
        _listenTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var reader = new StreamReader(server);
                    _ = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    onActivate();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(300, ct).ConfigureAwait(false);
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        _listenCts?.Cancel();
        try { _listenTask?.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
        _listenCts?.Dispose();
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); } catch { /* ignore */ }
        }
        _mutex.Dispose();
    }
}
