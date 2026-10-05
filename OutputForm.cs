using System.Drawing;

namespace WorkTunnel;

/// <summary>A simple read-only, monospaced window for command output.</summary>
internal sealed class OutputForm : Form
{
    public OutputForm(string title, string text)
    {
        Text = title;
        Width = 720;
        Height = 480;
        StartPosition = FormStartPosition.CenterScreen;
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5f),
            Text = text,
            BackColor = Color.White
        };
        Controls.Add(box);
    }
}
