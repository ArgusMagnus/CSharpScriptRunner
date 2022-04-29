dotnet publish -c Release -r win-x86 -o publish/win/x86 -p:ReleaseTag=dev --self-contained
dotnet publish -c Release -r win-x64 -o publish/win/x64 -p:ReleaseTag=dev --self-contained
# dotnet publish -c Release -o publish/any/any -p:ReleaseTag=dev