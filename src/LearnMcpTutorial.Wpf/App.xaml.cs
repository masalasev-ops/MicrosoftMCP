using System.Windows;
using LearnMcpTutorial.Wpf.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LearnMcpTutorial.Wpf;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Both files live at the repo root and are copied next to the binary.
        // appsettings.Local.json is gitignored and holds the real values; it
        // overrides the committed defaults. A key typed into the password box
        // still wins over both at runtime.
        var config = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<MainViewModel>();
        Services = services.BuildServiceProvider();

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }
}
