using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Pinboard.App.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const int MaxActivationPayloadBytes = 64 * 1024;
    private const string MutexName = "Pinboard.App.Singleton.7B0E2EA5";
    private const string PipeName = "Pinboard.App.Activation.7B0E2EA5";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();

    public SingleInstanceService()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        IsPrimary = createdNew;
    }

    public bool IsPrimary { get; }

    public async Task SendArgumentsAsync(string[] args)
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(3000);
        var json = JsonSerializer.Serialize(args);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaxActivationPayloadBytes)
        {
            throw new InvalidDataException("The Pinboard activation request is too large.");
        }
        await client.WriteAsync(bytes);
        await client.FlushAsync();
    }

    public void StartServer(Func<string[], Task> handler)
    {
        if (!IsPrimary)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cancellation.Token);
                    using var memory = new MemoryStream();
                    var buffer = new byte[4096];
                    while (true)
                    {
                        var count = await server.ReadAsync(buffer, _cancellation.Token);
                        if (count == 0)
                        {
                            break;
                        }
                        if (memory.Length + count > MaxActivationPayloadBytes)
                        {
                            throw new InvalidDataException("The Pinboard activation request is too large.");
                        }
                        await memory.WriteAsync(buffer.AsMemory(0, count), _cancellation.Token);
                    }
                    var args = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(memory.ToArray())) ?? [];
                    await handler(args);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(250, _cancellation.Token);
                }
            }
        });
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        if (IsPrimary)
        {
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }
}
