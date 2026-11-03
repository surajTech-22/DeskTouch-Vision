using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new DisplaySettingsForm());
    }
}
