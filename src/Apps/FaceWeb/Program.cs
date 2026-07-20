#region

using Avalonia;
using Avalonia.Browser;
using Face;

#endregion

await AppBuilder.Configure<App>()
                .WithInterFont()
                .StartBrowserAppAsync("out");