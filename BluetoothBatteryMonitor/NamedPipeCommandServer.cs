using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace BluetoothBatteryMonitor
{
    internal sealed class NamedPipeCommandServer : IAsyncDisposable
    {
        private readonly string _pipeName;
        private readonly Action<string> _commandHandler;
        private CancellationTokenSource? _shutdownCts;
        private Task? _acceptLoopTask;

        // Stores the pipe name and callback used to handle incoming tray commands.
        internal NamedPipeCommandServer(string pipeName, Action<string> commandHandler)
        {
            _pipeName = pipeName;
            _commandHandler = commandHandler;
        }

        // Starts the accept loop that waits for tray command connections.
        internal void Start()
        {
            if (_acceptLoopTask != null)
            {
                return;
            }

            _shutdownCts = new CancellationTokenSource();
            _acceptLoopTask = RunAcceptLoopAsync(_shutdownCts.Token);
        }

        // Accepts command connections and forwards each received line to the handler.
        private async Task RunAcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    // WaitForConnectionAsync blocks until a tray client opens the command pipe.
                    await pipe.WaitForConnectionAsync(cancellationToken);

                    using var reader = new StreamReader(pipe);
                    while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                    {
                        string? line = await reader.ReadLineAsync(cancellationToken);
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            break;
                        }

                        _commandHandler(line.Trim());
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(500, cancellationToken);
                }
            }
        }

        // Cancels the accept loop and waits for the last command session to exit.
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
        }
    }
}