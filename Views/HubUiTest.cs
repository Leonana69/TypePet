using System;
using System.IO;
using TypePet.Api.Hub;
using TypePet.Engine;

namespace TypePet.Views;

/// <summary>
/// Dev-only construction smoke for <see cref="HubView"/>. Builds the view and runs its row rendering on the
/// Avalonia UI services (the caller runs <c>SetupWithoutStarting()</c> first) — the exact path that threw a
/// NullReferenceException when the search box's observable fired during construction (now driven by a
/// TextChanged event that doesn't fire at construction, with the initial load deferred to attach). Invoked
/// via <c>--hub-ui-test</c>; no window is shown.
/// </summary>
public static class HubUiTest
{
    public static int Run()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected */ }

        string tmp = Path.Combine(Path.GetTempPath(), "TypePet_hubui_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // Local (offline) index source so nothing reaches the network even if a load is triggered.
            string indexPath = Path.Combine(tmp, "index.json");
            File.WriteAllText(indexPath, "{ \"commands\": [] }");
            var hub = new HubClient(tmp, indexPath);
            var store = new CommandStore(Path.Combine(tmp, "Commands"));
            var settings = new Settings { SourcePath = Path.Combine(tmp, "settings.json") };

            var view = new HubView(hub, store, settings, onChanged: () => { });
            view.RefreshInstalled();   // exercises RenderRows() — the line that crashed

            Console.WriteLine("OK: HubView constructed and rendered rows without throwing.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: HubView construction threw:\n" + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* best effort */ }
        }
    }
}
