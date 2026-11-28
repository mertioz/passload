using System.Drawing;
using System.Runtime.InteropServices;
using Passload.Core;
using Forms = System.Windows.Forms;

namespace Passload;

internal sealed class ShareTray : IDisposable
{
    private const uint Continuous = 0x80000000;
    private const uint SystemRequired = 0x00000001;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Icon _image;
    private bool _awake;
    private bool _disposed;

    public ShareTray(Action reopen, Action exit)
    {
        _image = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();
        _menu.Items.Add(Locale.Text("OpenPassload"), null, (_, _) => reopen());
        _menu.Items.Add(Locale.Text("QuitPassload"), null, (_, _) => exit());
        _icon = new Forms.NotifyIcon { Icon = _image, Text = "Passload", ContextMenuStrip = _menu };
        _icon.MouseDoubleClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) reopen(); };
    }

    public void Update(bool hidden, int links, bool busy)
    {
        if (_disposed) return;
        SetAwake(links > 0);
        if (!hidden || links == 0 && !busy)
        {
            _icon.Visible = false;
            return;
        }
        _icon.Text = links > 0 ? Locale.Text(links == 1 ? "TrayLinksOne" : "TrayLinksMany", links) : Locale.Text("TrayBusy");
        _menu.Items[0].Text = Locale.Text("OpenPassload");
        _menu.Items[1].Text = Locale.Text("QuitPassload");
        if (_icon.Visible) return;
        _icon.Visible = true;
        _icon.ShowBalloonTip(3500, Locale.Text("TrayBackground"), Locale.Text("TrayReopenHint"),
            Forms.ToolTipIcon.Info);
    }

    private void SetAwake(bool required)
    {
        if (required == _awake) return;
        if (SetThreadExecutionState(required ? Continuous | SystemRequired : Continuous) != 0) _awake = required;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SetAwake(false);
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _image.Dispose();
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
