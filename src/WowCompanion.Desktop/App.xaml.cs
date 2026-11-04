using System.Windows;
using WowCompanion.Core;
using WowCompanion.Data;
using System.Net.Http;

namespace WowCompanion.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.Default();
        paths.EnsureCreated();
        UserDatabase.Initialize(paths.UserDbPath);

        // Data product: "wowf" by default. Put another wago.tools product code (e.g. wow_cn_beta) in
        // %LOCALAPPDATA%\WowCompanion\data-product.txt to try a different/newer build.
        var productFile = System.IO.Path.Combine(paths.Root, "data-product.txt");
        var product = System.IO.File.Exists(productFile) ? System.IO.File.ReadAllText(productFile).Trim() : "";
        var options = product.Length > 0 ? new DataSourceOptions { Product = product } : new DataSourceOptions();
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WoWOracle/{MainViewModel.AppVersion} (free fan-made companion app)");
        var updater = new GameDataUpdater(new WagoClient(http, options), options, paths);
        IconCache.Initialize(paths.Root, http);
        MapImages.Initialize(paths.Root, http);
        TalentLayouts.Initialize(paths.Root, http);
        ListKeys.Register(); // the same arrow keys in every list

        new MainWindow(new MainViewModel(paths, updater)).Show();
    }
}
