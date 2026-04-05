using System.Drawing;
using System.Windows.Forms;

namespace PSControllerMonitor;

internal static class AppIconProvider
{
    private static readonly Icon SharedIcon = LoadApplicationIcon();

    // Returns a clone of the process icon so each form can own and dispose its own copy safely.
    internal static Icon CreateWindowIcon()
    {
        return (Icon)SharedIcon.Clone();
    }

    // Loads the icon embedded in the executable for use on forms and taskbar windows.
    private static Icon LoadApplicationIcon()
    {
        // Icon.ExtractAssociatedIcon reads the icon resources assigned to the running executable.
        Icon? icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (icon == null)
        {
            throw new InvalidOperationException("The application icon could not be loaded from the executable.");
        }

        return icon;
    }
}