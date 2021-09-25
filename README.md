# CSharpScriptRunner
A C# script engine for Windows which does not require admin privileges.

## Installation

### PowerShell

    $dir=md "$Env:Temp\{$(New-Guid)}"; $bkp=$ProgressPreference; $ProgressPreference='SilentlyContinue'; Write-Host 'Downloading...'; Invoke-WebRequest (Invoke-RestMethod -Uri 'https://api.github.com/repos/ArgusMagnus/CSharpScriptRunner/releases/latest' | select -Expand assets | select-string -InputObject {$_.browser_download_url} -Pattern '-win\.zip$' | Select -Expand Line -First 1) -OutFile "$dir\CSX.zip"; Write-Host 'Expanding archive...'; Expand-Archive -Path "$dir\CSX.zip" -DestinationPath "$dir"; & "$dir\x64\CSharpScriptRunner.exe" 'install'; Remove-Item $dir -Recurse; $ProgressPreference=$bkp; Write-Host 'Done'

