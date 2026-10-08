using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace PdfNote;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        global::WinRT.ComWrappersSupport.InitializeComWrappers();

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
