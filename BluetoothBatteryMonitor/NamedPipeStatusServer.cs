using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BluetoothBatteryMonitor
{
    internal sealed class NamedPipeStatusServer : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly string _pipeName;
        private readonly SemaphoreSlim _writerLock = new(1, 1);
        private NamedPipeServerStream? _currentPipe;
        private StreamWriter? _currentWriter;
        private MonitorStatus? _lastStatus;
        private CancellationTokenSource? _shutdownCts;
        private Task? _acceptLoopTask;

        // Stores the pipe name that the tray client connects to for status updates.
        internal NamedPipeStatusServer(string pipeName)
        {
            _pipeName = pipeName;
        }

        // Starts the accept loop that waits for a tray client to connect.
        internal void Start()
        {
            if (_acceptLoopTask != null)
            {
                return;
            }

            _shutdownCts = new CancellationTokenSource();
            _acceptLoopTask = RunAcceptLoopAsync(_shutdownCts.Token);
        }

        // Caches the latest status and schedules an async write to the connected client.
        internal void Publish(MonitorStatus status)
        {
            _lastStatus = status;
            _ = TryWriteStatusAsync(status);
        }

        // Accepts one client at a time and keeps the latest status flowing until it disconnects.
        private async Task RunAcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;

                try
                {
                    pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.Out,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    // WaitForConnectionAsync blocks until a client connects to this named pipe.
                    await pipe.WaitForConnectionAsync(cancellationToken);

                    var writer = new StreamWriter(pipe)
                    {
                        AutoFlush = true
                    };

                    await SetCurrentConnectionAsync(pipe, writer);

                    if (_lastStatus is MonitorStatus status)
                    {
                        await WriteStatusCoreAsync(writer, status, cancellationToken);
                    }

                    while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                    {
                        await Task.Delay(500, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(1000, cancellationToken);
                }
                finally
                {
                    await ClearCurrentConnectionAsync(pipe);
                    pipe?.Dispose();
                }
            }
        }

        // Writes a status update if a client is connected and the current writer is still valid.
        private async Task TryWriteStatusAsync(MonitorStatus status)
        {
            bool clearConnection = false;
            NamedPipeServerStream? pipeToClear = null;

            await _writerLock.WaitAsync();

            try
            {
                StreamWriter? writer = _currentWriter;
                NamedPipeServerStream? pipe = _currentPipe;
                if (writer == null || pipe == null || !pipe.IsConnected)
                {
                    return;
                }

                await WriteStatusCoreAsync(writer, status, CancellationToken.None);
            }
            catch
            {
                clearConnection = true;
                pipeToClear = _currentPipe;
            }
            finally
            {
                _writerLock.Release();
            }

            if (clearConnection)
            {
                await ClearCurrentConnectionAsync(pipeToClear);
            }
        }

        // Serializes the status payload as one JSON line for the tray client to read.
        private static async Task WriteStatusCoreAsync(StreamWriter writer, MonitorStatus status, CancellationToken cancellationToken)
        {
            // JsonSerializer.Serialize converts the status contract into the stable JSON boundary consumed by the tray app.
            string json = JsonSerializer.Serialize(StatusMessage.FromStatus(status), JsonOptions);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        }

        // Swaps in a newly connected pipe and writer under the server write lock.
        private async Task SetCurrentConnectionAsync(NamedPipeServerStream pipe, StreamWriter writer)
        {
            await _writerLock.WaitAsync();

            try
            {
                _currentPipe = pipe;
                _currentWriter = writer;
            }
            finally
            {
                _writerLock.Release();
            }
        }

        // Clears and disposes the current pipe connection if it still matches the supplied instance.
        private async Task ClearCurrentConnectionAsync(NamedPipeServerStream? pipe)
        {
            await _writerLock.WaitAsync();

            try
            {
                if (pipe != null && !ReferenceEquals(pipe, _currentPipe))
                {
                    return;
                }

                _currentWriter?.Dispose();
                _currentWriter = null;

                _currentPipe?.Dispose();
                _currentPipe = null;
            }
            finally
            {
                _writerLock.Release();
            }
        }

        // Cancels the accept loop and disposes the active pipe connection.
        public async ValueTask DisposeAsync()
        {
            if (_shutdownCts != null)
            {
                await _shutdownCts.CancelAsync();
                _shutdownCts.Dispose();
                _shutdownCts = null;
            }

            if (_acceptLoopTask != null)
            {
                try
                {
                    await _acceptLoopTask;
                }
                catch (OperationCanceledException)
                {
                }

                _acceptLoopTask = null;
            }

            await ClearCurrentConnectionAsync(_currentPipe);
            _writerLock.Dispose();
        }
    }
}