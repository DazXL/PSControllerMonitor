using System.IO.Pipes;
using System.Text.Json;

namespace PSControllerMonitor;

internal sealed class NamedPipeStatusClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _readerTask;

    internal event Action<StatusPayload>? StatusReceived;
    internal event Action<string>? ConnectionStateChanged;

    // Stores the status-pipe name that the tray client will reconnect to in the background.
    internal NamedPipeStatusClient(string pipeName)
    {
        _pipeName = pipeName;
    }

    // Starts the background read loop once.
    internal void Start()
    {
        if (_readerTask != null)
        {
            return;
        }

        _readerTask = RunAsync(_shutdown.Token);
    }

    // Pushes the UI back to the waiting state without tearing down the background loop.
    internal void RequestReconnect()
    {
        ConnectionStateChanged?.Invoke("Waiting for monitor...");
    }

    // Reconnects to the monitor pipe and raises status events for each valid JSON line received.
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ConnectionStateChanged?.Invoke("Waiting for monitor...");

                using var pipe = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.In,
                    PipeOptions.Asynchronous);

                // ConnectAsync waits for the monitor's named-pipe server to accept this client.
                await pipe.ConnectAsync(2000, cancellationToken);
                ConnectionStateChanged?.Invoke("Connected to monitor.");

                using var reader = new StreamReader(pipe);

                while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        break;
                    }

                    // JsonSerializer.Deserialize rebuilds the shared status contract from the pipe message text.
                    var envelope = JsonSerializer.Deserialize<StatusEnvelope>(line, JsonOptions);
                    if (!IsExpectedEnvelope(envelope))
                    {
                        continue;
                    }

                    StatusReceived?.Invoke(envelope!.Status!);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                ConnectionStateChanged?.Invoke("Waiting for monitor...");
            }

            try
            {
                await Task.Delay(1500, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    // Verifies that a JSON line matches the expected message type and version before the UI consumes it.
    private static bool IsExpectedEnvelope(StatusEnvelope? envelope)
    {
        return envelope?.Status != null &&
            string.Equals(envelope.Type, StatusMessageModels.MessageType, StringComparison.Ordinal) &&
            envelope.Version == StatusMessageModels.Version;
    }

    // Cancels the background reader loop and detaches faulted-task observation from the UI thread.
    public void Dispose()
    {
        _shutdown.Cancel();

        if (_readerTask != null)
        {
            _ = _readerTask.ContinueWith(
                static task =>
                {
                    _ = task.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _readerTask = null;
        }

    }
}