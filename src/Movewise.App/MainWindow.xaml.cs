using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Movewise.App.Services;
using Movewise.App.State;
using Movewise.M365;
using Movewise.M365.Auth;

namespace Movewise.App;

public partial class MainWindow : Window
{
    readonly MigrationService _migration;

    public MainWindow()
    {
        var options = MovewiseOptions.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddSingleton(options);
        services.AddSingleton(new AuthService(options, () => new WindowInteropHelper(this).Handle));
        services.AddSingleton<MigrationState>();
        services.AddSingleton<MigrationService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<AppRegistrationCreator>();
        var provider = services.BuildServiceProvider();
        _migration = provider.GetRequiredService<MigrationService>();
        Resources.Add("services", provider);

        InitializeComponent();

        // Quietly, once the window is up.
        Loaded += async (_, _) => await provider.GetRequiredService<UpdateService>().CheckAsync();
    }

    /// <summary>Closing mid-deployment is safe (the run can be resumed), but shouldn't happen by accident.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_migration.IsBusy)
        {
            var answer = MessageBox.Show(this,
                "A deployment is still writing to the destination tenant.\n\nIf you close Movewise now, the run stops. You can open it again from the Deploy screen to finish it or roll it back.\n\nClose anyway?",
                "Movewise", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                e.Cancel = true;
        }
        base.OnClosing(e);
    }
}
