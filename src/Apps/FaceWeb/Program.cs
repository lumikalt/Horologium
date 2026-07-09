using Avalonia;
using Avalonia.Browser;
using Face;

await AppBuilder.Configure<App>()
                .WithInterFont()
                .StartBrowserAppAsync("out");