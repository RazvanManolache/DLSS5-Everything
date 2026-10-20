using System.Reflection;

namespace Dlss5CompatApp.Tests;

public sealed class ArtworkLayoutTests
{
    [Theory]
    [InlineData(1060, 740)]
    [InlineData(1400, 900)]
    [InlineData(1920, 1080)]
    public void CoverControlsAndGridFitTheWindowAndKeepSelection(int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(preview: true) { Size = new(width, height) };
                form.Show(); Application.DoEvents();
                var games = Field<ListView>(form, "_games");
                var selected = games.Items[0];
                selected.Selected = true;
                Call(form, "SetConvenienceTiles", true);
                Application.DoEvents();
                var tools = Field<FlowLayoutPanel>(form, "_libraryViewTools");
                Assert.True(tools.Visible);
                Assert.True(form.ClientRectangle.Contains(form.RectangleToClient(tools.RectangleToScreen(tools.ClientRectangle))));
                foreach (var item in tools.Controls.OfType<Button>())
                {
                    Assert.True(item.Visible);
                    Assert.True(item.Bounds.Width >= 16);
                    Assert.True(tools.ClientRectangle.Contains(item.Bounds), item.AccessibleName + " is clipped");
                }
                Assert.Same(selected, Assert.Single(games.SelectedItems.Cast<ListViewItem>()));
                Assert.Equal(View.LargeIcon, games.View);
                Assert.Equal(new Size(156, 256), games.LargeImageList!.ImageSize);
                Assert.True(games.Items[1].Bounds.Left > games.Items[0].Bounds.Left, "Covers should flow across columns.");
                using var first = games.LargeImageList.Images[games.Items[0].ImageIndex];
                Assert.True(first.Width >= 144 && first.Height >= 234);
                Call(form, "SetConvenienceTiles", false);
                Assert.Equal(View.Details, games.View);
                Assert.Same(selected, Assert.Single(games.SelectedItems.Cast<ListViewItem>()));
                form.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static void Call(object target, string name, params object[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
