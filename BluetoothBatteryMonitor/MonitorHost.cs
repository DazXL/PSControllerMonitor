using System;
using System.Threading;
using System.Threading.Tasks;

namespace BluetoothBatteryMonitor
{
    public sealed class MonitorHost : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown = new();
        private readonly DualSenseMonitor _monitor = new();
        private readonly NamedPipeStatusServer _statusServer;
        private readonly NamedPipeCommandServer _commandServer;
        private Task? _monitorTask;
        private bool _started;

        // Builds the monitor host and wires its output into the two named-pipe servers.
        public MonitorHost()
        {
            _statusServer = new NamedPipeStatusServer(StatusMessage.PipeName);
            _commandServer = new NamedPipeCommandServer(StatusMessage.CommandPipeName, HandleCommand);
            _monitor.StatusChanged += _statusServer.Publish;
        }

        // Starts the background monitor loop and both named-pipe endpoints once.
        public void Start()
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _statusServer.Start();
            _commandServer.Start();
            _monitorTask = _monitor.RunAsync(_shutdown.Token);
        }

        // Routes incoming command-pipe messages to the appropriate monitor action.
        private void HandleCommand(string command)
        {
            if (string.Equals(command, StatusMessage.RotateControllersCommand, StringComparison.Ordinal))
            {
                _monitor.RotateControllerOrder();
            }
        }

        // Stops the monitor loop and disposes both named-pipe servers cleanly.
        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();

            if (_monitorTask != null)
            {
                try
                {
                    await _monitorTask;
                }
                catch (OperationCanceledException)
                {
                }

                _monitorTask = null;
            }

            _monitor.StatusChanged -= _statusServer.Publish;
            await _commandServer.DisposeAsync();
            await _statusServer.DisposeAsync();
            _shutdown.Dispose();
        }
    }
}