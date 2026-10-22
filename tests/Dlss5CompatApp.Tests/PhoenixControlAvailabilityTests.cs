using System.Reflection;
using System.Text.Json;

namespace Dlss5CompatApp.Tests;

public sealed class PhoenixControlAvailabilityTests
{
    [Theory]
    [InlineData((int)PhoenixSetting.Comparison, 0, false)]
    [InlineData((int)PhoenixSetting.Comparison, 1, true)]
    [InlineData((int)PhoenixSetting.Comparison, 5, true)]
    [InlineData((int)PhoenixSetting.BudgetMode, 0, true)]
    [InlineData((int)PhoenixSetting.BudgetMode, 1, false)]
    [InlineData((int)PhoenixSetting.Upscaler, 0, true)]
    [InlineData((int)PhoenixSetting.Upscaler, 1, false)]
    [InlineData((int)PhoenixSetting.Upscaler, 2, false)]
    [InlineData((int)PhoenixSetting.HookStage, 0, true)]
    [InlineData((int)PhoenixSetting.HookStage, 1, false)]
    [InlineData((int)PhoenixSetting.HookStage, 2, false)]
    [InlineData((int)PhoenixSetting.HookStage, 3, true)]
    public void DirectCaptureRejectsUnsupportedChoicesBeforeSending(int setting, int value, bool allowed)
    {
        var key = (PhoenixSetting)setting;
        var availability = PhoenixControlAvailability.FromSnapshot(JsonSerializer.SerializeToElement(new
            { history_policy = "spatial_reset_no_engine_motion_vectors" }));
        var settings = new PhoenixControlSettings().Set(key, value);
        if (allowed) availability.Validate(settings);
        else Assert.Throws<InvalidOperationException>(() => availability.Validate(settings));
        // A native engine snapshot does not inherit color-only restrictions.
        new PhoenixControlAvailability(false).Validate(settings);
    }

    [Fact]
    public void DirectCaptureChoicesAndNativeEngineChoicesStayDistinct() => Sta(() =>
    {
        using var workspace = new TestWorkspace();
        using var form = new PhoenixLiveForm(workspace.Root);
        var offline = new PhoenixOfflineControls(workspace.Root).Read();
        var load = typeof(PhoenixLiveForm).GetMethod("LoadSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        load.Invoke(form, [JsonSerializer.SerializeToElement(new
            { settings = offline.GetProperty("settings"), history_policy = "spatial_reset_no_engine_motion_vectors" })]);
        var comparison = Find(form, "Comparison");
        Assert.DoesNotContain(comparison.Items.Cast<PhoenixChoice>(), item => Equals(item.Value, 0) && !item.Label.Contains("inactive"));
        Assert.Single(Find(form, "BudgetMode").Items);
        Assert.Single(Find(form, "Upscaler").Items);
        load.Invoke(form, [offline]);
        Assert.Contains(comparison.Items.Cast<PhoenixChoice>(), item => Equals(item.Value, 0));
        Assert.Equal(3, Find(form, "Upscaler").Items.Count);
    });

    [Fact]
    public void ScreenCaptureKeepsRestrictionsWhenItsHelperOmitsRouteMetadata() => Sta(() =>
    {
        using var workspace = new TestWorkspace();
        using var form = new PhoenixLiveForm(workspace.Root,colorOnlyCapture:true);
        typeof(PhoenixLiveForm).GetMethod("LoadSnapshot",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(form,
            [new PhoenixOfflineControls(workspace.Root).Read()]);
        Assert.Single(Find(form,"Upscaler").Items);
        Assert.Single(Find(form,"BudgetMode").Items);
        Assert.DoesNotContain(Find(form,"Comparison").Items.Cast<PhoenixChoice>(), item=>Equals(item.Value,0) && !item.Label.Contains("inactive"));
    });

    [Fact]
    public void HelperDeviceCatalogueControlsBothValidationAndVisibleChoices() => Sta(() =>
    {
        using var workspace = new TestWorkspace();
        using var form = new PhoenixLiveForm(workspace.Root);
        var offline = new PhoenixOfflineControls(workspace.Root).Read();
        var snapshot=JsonSerializer.SerializeToElement(new { settings=offline.GetProperty("settings"),
            helper=new {execution_devices=new[] {new {id="native",label="Rendering GPU"},new {id="cpu",label="CPU reference"}}}});
        var availability=PhoenixControlAvailability.FromSnapshot(snapshot);
        availability.Validate(new PhoenixControlSettings().Set(PhoenixSetting.ExecutionDevice,"cpu"));
        Assert.Throws<InvalidOperationException>(()=>availability.Validate(new PhoenixControlSettings().Set(PhoenixSetting.ExecutionDevice,"sycl:cpu")));
        typeof(PhoenixLiveForm).GetMethod("LoadSnapshot",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(form,[snapshot]);
        Assert.Equal(new[]{"native","cpu"},Find(form,"ExecutionDevice").Items.Cast<PhoenixChoice>().Select(item=>(string)item.Value));
    });

    [Fact]
    public void SavedVendorRequestShowsItsFallbackWithoutSilentlyRewritingConfiguration() => Sta(() =>
    {
        using var workspace = new TestWorkspace();
        var ini = workspace.FilePath("ReShade.ini");
        const string original = "[Phoenix]\nUpscaler=1\n";
        File.WriteAllText(ini, original);
        using var form = new PhoenixLiveForm(workspace.Root);
        var offline = new PhoenixOfflineControls(workspace.Root).Read();
        typeof(PhoenixLiveForm).GetMethod("LoadSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form,
            [JsonSerializer.SerializeToElement(new { settings = offline.GetProperty("settings"),
                history_policy = "spatial_reset_no_engine_motion_vectors" })]);
        var combo = Find(form, "Upscaler");
        var selected = Assert.IsType<PhoenixChoice>(combo.SelectedItem);
        Assert.Equal(1, selected.Value);
        Assert.Contains("inactive", selected.Label);
        Assert.Contains(combo.Items.Cast<PhoenixChoice>(), choice => Equals(choice.Value, 0));
        Assert.DoesNotContain(combo.Items.Cast<PhoenixChoice>(), choice => Equals(choice.Value, 2));
        Assert.Equal(original, File.ReadAllText(ini));
    });

    static ComboBox Find(Control root, string name) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child))).OfType<ComboBox>().Single(control => control.Name == name);
    static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));
    static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Availability UI timed out");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
