using System.Windows;

namespace RegistrationTester;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var address = e.Args.Length == 0 ? "https://lineapibot.onrender.com/" : e.Args.FirstOrDefault();
        if (e.Args.Length > 1 || !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)) ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
        {
            MessageBox.Show("ระบุ URL หลักของ API แบบ HTTPS หรือ HTTP บน localhost", "LINE Registration Tester",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        MainWindow = new MainWindow(new RegistrationApi(uri));
        MainWindow.Show();
    }
}

