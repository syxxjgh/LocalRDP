using System.Windows;
using DeskClone.ChildSession;
using DeskClone.Core;
using DeskClone.Logging;
using DeskClone.ViewModels;
using DeskClone.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeskClone;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddSingleton<SettingsService>();
        services.AddSingleton(typeof(ILogger<>), typeof(FileLogger<>));
        services.AddSingleton<ChildSessionService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        _serviceProvider = services.BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnSessionEnding(System.Windows.SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);

        // 系统注销/关机时不弹退出确认框：直接放行关机，并标记保留分身——
        // 系统会自行结束所有会话（分身会话跟随主桌面注销），程序不应拦截。
        _serviceProvider?.GetService<ChildSessionService>()?.PreserveChildSessionOnExit();
        if (MainWindow is Views.MainWindow window)
        {
            window.AllowClose = true;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Dispose 会断开 RDP 并注销 Child Session，分身中的程序会随会话一起退出。
        _serviceProvider?.GetService<ChildSessionService>()?.Dispose();
        _serviceProvider?.GetService<SettingsService>()?.Save();
        _serviceProvider?.Dispose();
        _serviceProvider = null;
        base.OnExit(e);
    }
}
