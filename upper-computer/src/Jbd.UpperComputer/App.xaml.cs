using System.Windows;
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
        ViewModelLocationProvider.Register<MainWindow, MainViewModel>();
    }
}
