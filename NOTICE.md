# Third-party components

Loadout uses the .NET runtime, Windows Desktop (WPF and the Windows Forms tray icon), and System.Management from Microsoft’s open-source .NET projects.

- .NET runtime: https://github.com/dotnet/runtime
- WPF: https://github.com/dotnet/wpf
- Windows Forms: https://github.com/dotnet/winforms
- System.Management package: https://www.nuget.org/packages/System.Management/10.0.0

The supplied portable download includes dependency licenses and notices in its `licenses` folder. Loadout’s own code is licensed under the MIT license in `LICENSE`. When distributing a newly built runtime version, include the corresponding runtime license and third-party notices from its NuGet packages.
