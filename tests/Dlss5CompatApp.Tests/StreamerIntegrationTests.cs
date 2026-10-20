using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public class StreamerIntegrationTests
{
    [Fact]
    public void PairingMatchesAndroidContractAndDefaultsToNativePyrowave()
    {
        using var json=JsonDocument.Parse(StreamerPairing.Serialize(new(IPAddress.Loopback,47855),new('A',64),new('B',64),true));
        var root=json.RootElement;
        Assert.Equal("pyrowave",root.GetProperty("codec").GetString());
        Assert.Equal("follow",root.GetProperty("captureMode").GetString());
        Assert.Equal(4096,root.GetProperty("maxEdge").GetInt32());
        Assert.Equal(1,root.GetProperty("opacity").GetInt32());
        Assert.Equal(100,root.GetProperty("bitrate").GetInt32());
        Assert.Equal("127.0.0.1",root.GetProperty("host").GetString());
    }
    [Fact]
    public void PairingFallsBackExplicitlyToRawWhenPcCodecIsAbsent()
    {
        using var json=JsonDocument.Parse(StreamerPairing.Serialize(new(IPAddress.Loopback,47855),new('A',64),new('B',64),false));
        Assert.Equal("raw",json.RootElement.GetProperty("codec").GetString());
    }
    [Fact]
    public void PairingRejectsInvalidEndpointAndUnstartedIdentity()
    {
        Assert.Throws<InvalidDataException>(()=>StreamerPairing.Serialize(new(IPAddress.Parse("8.8.8.8"),47855),new('A',64),new('B',64),true));
        Assert.Throws<InvalidDataException>(()=>StreamerPairing.Serialize(new(IPAddress.Loopback,47855),"","",true));
    }
    [Fact]
    public void CommandLinePairingExportNeverOverwritesAnExistingFile()
    {
        using var workspace=new TestWorkspace();var path=workspace.FilePath("pairing.json");
        StreamerPairing.WriteNew(path,[1,2,3]);
        Assert.Throws<IOException>(()=>StreamerPairing.WriteNew(path,[4]));
        Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(path));
    }
    [Fact]
    public void AddressSelectionPrefersRealWifiOverVirtualAdaptersAndLoopback()
    {
        var wifi=IPAddress.Parse("192.168.1.20");
        var addresses=LanFrameView.OrderAddresses([
            (IPAddress.Parse("172.16.1.1"),NetworkInterfaceType.Ethernet,true),
            (IPAddress.Parse("10.0.0.2"),NetworkInterfaceType.Ethernet,false),
            (wifi,NetworkInterfaceType.Wireless80211,false),
            (wifi,NetworkInterfaceType.Wireless80211,false),
            (IPAddress.Parse("8.8.8.8"),NetworkInterfaceType.Ethernet,false)]);
        Assert.Equal(wifi,addresses[0]);Assert.Equal(IPAddress.Loopback,addresses[^1]);Assert.Equal(4,addresses.Length);
    }
    [Theory]
    [InlineData(1060,740)]
    [InlineData(1400,900)]
    [InlineData(1920,1080)]
    public void MainApplicationContainsWorkingStreamerNavigation(int width,int height)
    {
        Exception? failure=null;var thread=new Thread(()=> {
            try {
                using var form=new MainForm(preview:true){Size=new(width,height),Location=new(-20000,-20000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false};
                form.Show();Application.DoEvents();
                var show=typeof(MainForm).GetMethod("ShowPage",BindingFlags.NonPublic|BindingFlags.Instance)!;
                show.Invoke(form,["LAN"]);Application.DoEvents();
                var active=(string)typeof(MainForm).GetField("_activePage",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form)!;
                Assert.Equal("Streamer",active);
                var view=Descendants(form).OfType<LanFrameView>().Single();Assert.True(view.Visible);
                Assert.Throws<InvalidOperationException>(view.PairingData);
                foreach(var palette in new[]{UiPalette.Dark,UiPalette.Light}) {
                    UiTheme.Apply(form,palette);Application.DoEvents();
                    using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,form.ClientRectangle);
                    var start=Descendants(view).OfType<Button>().Single(b=>b.AccessibleName=="Start streamer");
                    Assert.True(start.Visible);Assert.True(start.Bounds.Width>60);
                    foreach(var button in Descendants(view).OfType<Button>().Where(b=>b.Visible))
                        Assert.True(button.Bottom<=button.Parent!.ClientSize.Height,$"Clipped streamer button: {button.Text}");
                }
                form.Close();
            }catch(Exception error){failure=error;}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    static IEnumerable<Control> Descendants(Control root)=>root.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Descendants(c)));
}
