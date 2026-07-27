#region

using Avalonia;
using Chip8Face;

#endregion

AppBuilder.Configure<App>()
          .UsePlatformDetect()
          .WithInterFont()
          .LogToTrace()
          .StartWithClassicDesktopLifetime(args);