# Phoenix MT5 Update Installer

Standalone Windows installer for Phoenix EA updates. It detects a single Phoenix build .bin in a MetaTrader MQL5\\Files folder, asks before closing MetaTrader, backs up the existing EX5, installs the update, and reopens the same terminal. CLI arguments are also supported for EA integration.

Build: dotnet publish PhoenixInstaller/PhoenixInstaller.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
