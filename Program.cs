using System;
using System.Windows.Forms;

namespace MuteBarBridge;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApplicationContext());
        }
        catch (Exception ex)
        {
            // This guarantees that even if the console fails, a physical Windows error box will pop up on your screen
            MessageBox.Show(
                $"The application crashed on startup:\n\n{ex}",
                "MuteBarBridge Fatal Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }
}
