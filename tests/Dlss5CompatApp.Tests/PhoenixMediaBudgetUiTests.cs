using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Dlss5CompatApp;

namespace Dlss5CompatApp.Tests;

public sealed class PhoenixMediaBudgetUiTests
{
    [Fact]
    public void ReplacedPackagesRefreshDevicesAndOldQueriesCannotOverwriteTheNewInventory()
    {
        Exception? failure=null;
        var thread=new Thread(()=>{
            var root=Path.Combine(Path.GetTempPath(),"phoenix-device-refresh-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try{
                var manifest=Path.Combine(root,"phoenix-runtime.json");File.WriteAllText(manifest,"{\"build\":1}");
                var exe=Path.Combine(root,"PhoenixVideoPlayer.exe");
                var calls=new List<TaskCompletionSource<IReadOnlyList<PhoenixMediaPlayer.Device>>>();
                using var view=new PhoenixMediaView(()=>null,true,(_,_)=>{
                    var completion=new TaskCompletionSource<IReadOnlyList<PhoenixMediaPlayer.Device>>();calls.Add(completion);return completion.Task;});
                var choices=Descendants(view).OfType<ComboBox>().Single(x=>x.Items.Count>0&&x.Items[0] is PhoenixMediaPlayer.Device);
                void Pump(Task task){var deadline=DateTime.UtcNow.AddSeconds(10);while(!task.IsCompleted&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(1);}Assert.True(task.IsCompleted);task.GetAwaiter().GetResult();}
                var old=view.RefreshExecutionDevices(exe,root);Assert.False(choices.Enabled);
                File.WriteAllText(manifest,"{\"build\":2}");
                var replacement=view.RefreshExecutionDevices(exe,root);Assert.Equal(2,calls.Count);
                calls[1].SetResult([new("cpu","New CPU"),new("vulkan:new","New GPU")]);Pump(replacement);
                calls[0].SetResult([new("vulkan:old","Obsolete GPU")]);Pump(old);
                Assert.True(choices.Enabled);Assert.Contains(choices.Items.Cast<PhoenixMediaPlayer.Device>(),x=>x.Id=="vulkan:new");
                Assert.DoesNotContain(choices.Items.Cast<PhoenixMediaPlayer.Device>(),x=>x.Id=="vulkan:old");
                Pump(view.RefreshExecutionDevices(exe,root));Assert.Equal(2,calls.Count); // Cached matching identity.
                choices.SelectedIndex=choices.Items.Cast<PhoenixMediaPlayer.Device>().ToList().FindIndex(x=>x.Id=="cpu");
                File.WriteAllText(manifest,"{\"build\":3}");
                var inFlight=view.RefreshExecutionDevices(exe,root);Assert.Equal(3,calls.Count);
                // The package changes with no frontend refresh event while the helper runs.
                File.WriteAllText(manifest,"{\"build\":4}");calls[2].SetResult([new("cpu","Stale CPU")]);
                var limit=DateTime.UtcNow.AddSeconds(10);while(calls.Count<4&&DateTime.UtcNow<limit){Application.DoEvents();Thread.Sleep(1);}
                Assert.Equal(4,calls.Count);calls[3].SetResult([new("cpu","CPU"),new("vulkan:new","Current GPU")]);Pump(inFlight);
                Assert.Equal("cpu",((PhoenixMediaPlayer.Device)choices.SelectedItem!).Id);
                File.WriteAllText(manifest,"{\"build\":5}");var failed=view.RefreshExecutionDevices(exe,root);
                calls[4].SetException(new IOException("Injected helper failure"));Pump(failed);
                Assert.True(choices.Enabled);Assert.Single(choices.Items);
                var retry=view.RefreshExecutionDevices(exe,root);Assert.Equal(6,calls.Count);calls[5].SetResult([new("cpu","Recovered CPU"),new("vulkan:other","Another GPU")]);Pump(retry);
                Assert.Contains(choices.Items.Cast<PhoenixMediaPlayer.Device>(),x=>x.Id=="cpu");
                Assert.Equal("cpu",((PhoenixMediaPlayer.Device)choices.SelectedItem!).Id);
            }catch(Exception error){failure=error;}finally{Directory.Delete(root,true);}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)),"Device refresh did not complete");
        if(failure is not null)ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [Fact]
    public void BothModelsCanUseNeuralBudgetsWithoutPipelineMonitoring()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var view = new PhoenixMediaView(() => null, preview: true);
                var controls = Descendants(view).ToArray();
                NumericUpDown Number(string label) => controls.OfType<Label>().Single(x => x.Text == label)
                    .Parent!.Controls.OfType<NumericUpDown>().Single();
                ComboBox Choice(string first) => controls.OfType<ComboBox>()
                    .Single(x => x.Items.Count > 0 && x.Items[0]?.ToString() == first);
                var target = Number("Target source FPS (0 = no limit)");
                var maximum = Number("Maximum processing ms (0 = no limit)");
                var minimum = Number("Minimum central blocks");
                var age = Number("Processing age budget (ms)");
                var monitor = Choice("Monitoring off");
                var priority = Choice("Keep quality fixed");
                foreach (int model in new[] { 0, 1 })
                {
                    Choice("Original").SelectedIndex = model;
                    monitor.SelectedIndex = 0;
                    Assert.True(target.Enabled && maximum.Enabled && minimum.Enabled);
                    Assert.False(age.Enabled || priority.Enabled);
                    target.Value = 30; maximum.Value = 12; minimum.Value = 3;
                    monitor.SelectedIndex = 1; priority.SelectedIndex = 1;
                    Assert.True(age.Enabled);
                    monitor.SelectedIndex = 0;
                    Assert.False(age.Enabled);
                    Assert.True(target.Enabled && maximum.Enabled && minimum.Enabled);
                    Assert.Equal(30, target.Value); Assert.Equal(12, maximum.Value); Assert.Equal(3, minimum.Value);
                }
                // The full-resolution and repeat controls share these bounds.
                Number("Central blocks").Value = 2;
                Assert.Equal(2, minimum.Maximum); Assert.Equal(2, minimum.Value);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Media controls did not initialize");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
