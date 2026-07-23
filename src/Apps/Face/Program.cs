#region

using Avalonia;
using Face;

#endregion

AppBuilder.Configure<App>()
          .UsePlatformDetect()
          .WithInterFont()
          .LogToTrace()
          .StartWithClassicDesktopLifetime(args);