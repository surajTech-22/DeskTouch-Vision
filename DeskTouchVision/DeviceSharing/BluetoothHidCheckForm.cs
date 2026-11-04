using System.Drawing;
using System.Windows.Forms;

namespace DeskTouch.DeviceShare;

/// <summary>
/// Read-only report collected by the already authenticated DeskTouch mesh.
/// Opening this form never claims keyboard/mouse authority or changes pairing.
/// </summary>
internal sealed class BluetoothHidCheckForm : Form
{
    private readonly Func<IReadOnlyList<BluetoothHidDeviceStatus>> _read;
    private readonly Action _refresh;
    private readonly ListView _devices = new() {
        View=View.Details, FullRowSelect=true, MultiSelect=false,
        Dock=DockStyle.Fill, HeaderStyle=ColumnHeaderStyle.Nonclickable,
        BackColor=Color.FromArgb(40,42,49), ForeColor=Color.WhiteSmoke,
        BorderStyle=BorderStyle.None
    };
    private int? _previousCandidate;
    private readonly Label _message = new() {
        Dock=DockStyle.Bottom, Height=115, Padding=new Padding(14,10,14,8),
        ForeColor=Color.FromArgb(189,202,216)
    };

    internal BluetoothHidCheckForm(
        Func<IReadOnlyList<BluetoothHidDeviceStatus>> read, Action refresh)
    {
        _read=read;_refresh=refresh;
        Text="DeskTouch — Bluetooth keyboard/mouse check";
        Font=new Font("Segoe UI",10);
        BackColor=Color.FromArgb(31,32,36);
        ForeColor=Color.WhiteSmoke;
        Size=new Size(850,465);
        MinimumSize=new Size(630,390);
        StartPosition=FormStartPosition.CenterParent;
        _devices.Columns.Add("Connected Windows PC",250);
        _devices.Columns.Add("Bluetooth LE",130);
        _devices.Columns.Add("Peripheral role",145);
        _devices.Columns.Add("Eligibility",180);
        var title=new Label {
            Dock=DockStyle.Top,Height=79,Padding=new Padding(15,12,15,5),
            Text="Bluetooth HID eligibility across connected Windows PCs\n" +
                "Each PC checks its own adapter; results arrive over DeskTouch's secure connection.",
            ForeColor=Color.WhiteSmoke
        };
        var actions=new Panel{Dock=DockStyle.Bottom,Height=53};
        var again=new Button{
            Text="Recheck connected PCs",Dock=DockStyle.Left,
            Width=210,Height=35,Margin=new Padding(12,8,8,8),
            FlatStyle=FlatStyle.Flat,
            BackColor=Color.FromArgb(40,101,173),ForeColor=Color.WhiteSmoke
        };
        again.Click+=(_,_)=>{_refresh();RefreshReports();};
        actions.Padding=new Padding(14,8,8,5);
        actions.Controls.Add(again);
        Controls.Add(_devices);
        Controls.Add(_message);
        Controls.Add(actions);
        Controls.Add(title);
        RefreshReports();
    }

    internal void RefreshReports()
    {
        if(IsDisposed)return;
        var reports=_read();
        var preferred=BluetoothHidSelection.PreferredCandidate(reports,_previousCandidate);
        _previousCandidate=preferred;
        _devices.BeginUpdate();
        try
        {
            _devices.Items.Clear();
            foreach(var r in reports)
            {
                var item=new ListViewItem(r.Name);
                item.SubItems.Add(r.Capability.LowEnergy switch
                    {true=>"Yes",false=>"No",_=>"Unknown"});
                item.SubItems.Add(r.Capability.Peripheral switch
                    {true=>"Yes",false=>"No",_=>"Unknown"});
                item.SubItems.Add(r.Capability.State switch {
                    "Candidate"=>"CANDIDATE (not paired)",
                    "Unsupported"=>"Not supported",
                    "NoAdapter"=>"No Bluetooth adapter",
                    "Checking"=>"Checking…",
                    _=>"Unable to verify"
                });
                item.ToolTipText=r.Capability.Detail;
                _devices.Items.Add(item);
            }
        }
        finally{_devices.EndUpdate();}
        if(reports.Count==0)
        {
            _message.Text="No Windows PCs reported yet. Keep both PCs connected, then select Recheck connected PCs.";
            return;
        }
        var chosen=reports.FirstOrDefault(r=>r.Id==preferred);
        _message.Text=chosen==null
            ? "No Windows PC has confirmed BLE peripheral eligibility. Check the adapter details and connectivity."
            : "Preferred eligible adapter: "+chosen.Name+
              ". This is a hardware CAPABILITY check, not a live Bluetooth HID link. "+
              "Actual Android pairing, HID reports and RSSI are not tested yet.";
        _message.Text+="\nOne connected eligible PC is preferred automatically. Signal strength can be compared "+
            "only after paired HID endpoints provide real measurements.";
    }
}
