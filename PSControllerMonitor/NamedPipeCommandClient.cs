using System.IO.Pipes;
using System.Text;

namespace PSControllerMonitor;

internal sealed class NamedPipeCommandClient
{
    private readonly string _pipeName;

    // Stores the command-pipe name used to send tray actions back to the monitor.
    internal NamedPipeCommandClient(string pipeName)
    {
        _pipeName = pipeName;
    }

    // Connects to the command pipe, sends one line, and reports whether the write succeeded.
    internal async Task<bool> SendAsync(string command, CancellationToken cancellationToken = default)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            // ConnectAsync waits for the monitor's command pipe to accept the outgoing client connection.
            await pipe.ConnectAsync(1000, cancellationToken);

            byte[] buffer = Encoding.UTF8.GetBytes(command + Environment.NewLine);
            await pipe.WriteAsync(buffer, cancellationToken);
            await pipe.FlushAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }
}