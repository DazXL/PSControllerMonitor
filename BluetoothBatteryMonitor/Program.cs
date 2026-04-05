using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BluetoothBatteryMonitor
{
    class Program
    {
        private static string? s_lastScreen;
        private static int s_lastScreenLineCount;
        private static Mutex? s_singleInstanceMutex;

        // Starts the monitor console app, status pipe, command pipe, and console hotkey loop.
        static async Task Main(string[] args)
        {
            // Keep a single monitor instance alive so repeated runs do not compete for the same HID paths.
            if (!TryAcquireSingleInstance())
            {
                RenderStatus(MonitorStatus.Single(new ControllerStatus(
                    ControllerStateKind.Error,
                    "Sony Controller Monitor",
                    ControllerFamily.UnknownSony.ToDisplayName(),
                    false,
                    null,
                    null,
                    null,
                    "Another monitor instance is already running.",
                    null,
                    null,
                    "Controller monitor already running",
                    [],
                    DateTimeOffset.Now)));
                await Task.Delay(2000);
                return;
            }

            var monitor = new DualSenseMonitor();
            using var shutdownCts = new CancellationTokenSource();
            await using var pipeServer = new NamedPipeStatusServer(StatusMessage.PipeName);
            await using var commandServer = new NamedPipeCommandServer(StatusMessage.CommandPipeName, command =>
            {
                if (string.Equals(command, StatusMessage.RotateControllersCommand, StringComparison.Ordinal))
                {
                    monitor.RotateControllerOrder();
                }
            });
            monitor.StatusChanged += RenderStatus;
            monitor.StatusChanged += pipeServer.Publish;
            pipeServer.Start();
            commandServer.Start();
            Task hotkeyTask = ListenForHotkeysAsync(monitor, shutdownCts.Token);

            try
            {
                await monitor.RunAsync(shutdownCts.Token);
            }
            finally
            {
                shutdownCts.Cancel();
                try
                {
                    await hotkeyTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        // Claims the process-wide mutex so only one monitor instance can run at a time.
        private static bool TryAcquireSingleInstance()
        {
            // A mutex is safer than killing sibling processes and avoids fighting over live HID handles.
            s_singleInstanceMutex = new Mutex(initiallyOwned: true, "Local\\BluetoothBatteryMonitor.SingleInstance", out bool createdNew);
            if (createdNew)
            {
                return true;
            }

            s_singleInstanceMutex.Dispose();
            s_singleInstanceMutex = null;
            return false;
        }

        // Listens for Ctrl+L in the console and rotates the controller display order when pressed.
        private static async Task ListenForHotkeysAsync(DualSenseMonitor monitor, CancellationToken cancellationToken)
        {
            if (Console.IsInputRedirected)
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    while (Console.KeyAvailable)
                    {
                        ConsoleKeyInfo keyInfo = Console.ReadKey(intercept: true);
                        if (keyInfo.Key == ConsoleKey.L && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
                        {
                            monitor.RotateControllerOrder();
                        }
                    }

                    await Task.Delay(100, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
            }
        }

        // Rebuilds the console screen from the latest monitor status snapshot.
        private static void RenderStatus(MonitorStatus status)
        {
            const string title = "Sony Controller Monitor";
            string[] lines = BuildLines(status);

            // Avoid repainting the console when the screen contents have not changed.
            string[] screenLines = new string[lines.Length + 3];
            screenLines[0] = string.Empty;
            screenLines[1] = $"--- {title} ---";
            Array.Copy(lines, 0, screenLines, 2, lines.Length);
            screenLines[^1] = "----------------------------------";
            string renderedScreen = string.Join(Environment.NewLine, screenLines);

            if (string.Equals(s_lastScreen, renderedScreen, StringComparison.Ordinal))
            {
                return;
            }

            s_lastScreen = renderedScreen;
            RewriteConsoleViewport(renderedScreen, screenLines.Length);
        }

        // Chooses between in-place repainting and normal redirected output.
        private static void RewriteConsoleViewport(string renderedScreen, int currentLineCount)
        {
            if (!Console.IsOutputRedirected)
            {
                RewriteConsoleInPlace(renderedScreen, currentLineCount);
                return;
            }

            Console.Clear();
            Console.WriteLine(renderedScreen);
            s_lastScreenLineCount = currentLineCount;
        }

        // Repaints the console without scrolling so the monitor behaves like a live dashboard.
        private static void RewriteConsoleInPlace(string renderedScreen, int currentLineCount)
        {
            try
            {
                // Console.SetCursorPosition rewinds output so the dashboard can be redrawn in place.
                Console.CursorVisible = false;
                Console.SetCursorPosition(0, 0);

                string[] lines = renderedScreen.Split(Environment.NewLine);
                int writeWidth = GetSafeConsoleWidth();

                foreach (string line in lines)
                {
                    string paddedLine = PadForConsoleWidth(line, writeWidth);
                    Console.Write(paddedLine);
                    Console.WriteLine();
                }

                int extraLineCount = Math.Max(0, s_lastScreenLineCount - currentLineCount);
                string blankLine = new(' ', Math.Max(0, writeWidth - 1));
                for (int lineIndex = 0; lineIndex < extraLineCount; lineIndex++)
                {
                    Console.Write(blankLine);
                    Console.WriteLine();
                }

                Console.SetCursorPosition(0, 0);
                s_lastScreenLineCount = currentLineCount;
            }
            catch
            {
                Console.Clear();
                Console.WriteLine(renderedScreen);
                s_lastScreenLineCount = currentLineCount;
            }
        }

        // Reads a safe console width with a fallback for redirected or unsupported terminals.
        private static int GetSafeConsoleWidth()
        {
            try
            {
                return Math.Max(Console.WindowWidth, 40);
            }
            catch
            {
                return 120;
            }
        }

        // Truncates or pads a line so each dashboard repaint fully overwrites the previous one.
        private static string PadForConsoleWidth(string line, int consoleWidth)
        {
            int targetWidth = Math.Max(0, consoleWidth - 1);
            if (targetWidth == 0)
            {
                return string.Empty;
            }

            if (line.Length >= targetWidth)
            {
                return line[..targetWidth];
            }

            return line.PadRight(targetWidth);
        }

        // Builds the controller heading shown above each console status block.
        private static string BuildTitle(ControllerStatus status)
        {
            return string.IsNullOrWhiteSpace(status.ConnectionType) || string.Equals(status.ConnectionType, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? status.DisplayName
                : $"{status.DisplayName} {status.ConnectionType}";
        }

        // Expands the current monitor status into the list of console lines to render.
        private static string[] BuildLines(MonitorStatus monitorStatus)
        {
            ControllerStatus status = monitorStatus.PrimaryController;
            var lines = new List<string>();

            if (ShouldShowControllerLabel(status))
            {
                AppendControllerBlock(lines, status, 1, indent: true);
            }
            else
            {
                lines.Add(status.SummaryText);

                if (!string.IsNullOrWhiteSpace(status.DetailText))
                {
                    lines.Add(status.DetailText);
                }
            }

            if (monitorStatus.OtherControllers.Length > 0)
            {
                for (int index = 0; index < monitorStatus.OtherControllers.Length; index++)
                {
                    ControllerStatus otherController = monitorStatus.OtherControllers[index];
                    AppendControllerBlock(lines, otherController, index + 2, indent: true);
                }
            }

            if (ShouldShowControllerLabel(status))
            {
                lines.Add(string.Empty);
                lines.Add("(press Ctrl+L to re-order)");
            }

            return lines.ToArray();
        }

        // Appends one controller's details to the console line buffer.
        private static void AppendControllerBlock(List<string> lines, ControllerStatus status, int controllerNumber, bool indent)
        {
            string prefix = indent ? "  " : string.Empty;

            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"Controller {controllerNumber}: {BuildTitle(status)}");
            lines.Add($"{prefix}{status.SummaryText}");

            if (!string.IsNullOrWhiteSpace(status.DetailText))
            {
                lines.Add($"{prefix}{status.DetailText}");
            }

            if (status.IsConnected && !string.IsNullOrWhiteSpace(status.ChargingText))
            {
                lines.Add($"{prefix}Status:  {status.ChargingText}");
            }

            if (status.IsConnected && !string.IsNullOrWhiteSpace(status.BatteryText))
            {
                lines.Add($"{prefix}Battery: {status.BatteryText}");
            }

            foreach (string diagnostic in status.Diagnostics)
            {
                lines.Add($"{prefix}{diagnostic}");
            }

            if (!string.IsNullOrWhiteSpace(status.DeviceId))
            {
                lines.Add($"{prefix}HID ID: {status.DeviceId}");
            }
        }

        // Decides whether the generic monitor placeholder should be rendered as a named controller block.
        private static bool ShouldShowControllerLabel(ControllerStatus status)
        {
            return !string.Equals(status.DisplayName, "Sony Controller Monitor", StringComparison.Ordinal);
        }
    }
}