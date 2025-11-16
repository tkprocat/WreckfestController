using System.Windows;

namespace WreckfestController;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The MainWindow will be created automatically due to StartupUri in App.xaml
    }
}
