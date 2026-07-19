using System.Windows;
using Jbd.UpperComputer.Services;
using Jbd.UpperComputer.ViewModels;
using Jbd.UpperComputer.Views;
using Prism.DryIoc;
using Prism.Ioc;
using Prism.Mvvm;

namespace Jbd.UpperComputer;

public partial class App : PrismApplication
{
    protected override Window CreateShell() => Container.Resolve<MainWindow>();

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<ISerialBmsClient, SerialBmsClient>();
        containerRegistry.RegisterSingleton<IConfigCaptureService, ConfigCaptureService>();
        ViewModelLocationProvider.Register<MainWindow, MainViewModel>();
    }
}
