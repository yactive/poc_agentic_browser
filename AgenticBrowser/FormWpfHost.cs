using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace AgenticBrowser;

public class FormWpfHost : Form
{
    public FormWpfHost()
    {
        Text = "Active Worker";
        Width = 1500;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = System.Drawing.Color.FromArgb(13, 13, 26); // #0D0D1A

        var host = new ElementHost
        {
            Dock = DockStyle.Fill,
            Child = new MainView()
        };
        Controls.Add(host);
    }
}
