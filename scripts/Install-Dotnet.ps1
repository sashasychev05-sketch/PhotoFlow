# Portable SDK installation for PhotoFlow only. Does not change PATH or installed system SDKs.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectWork = [IO.Path]::GetFullPath((Join-Path $repoRoot '..\work'))
New-Item -ItemType Directory -Path $projectWork -Force | Out-Null
$metadata = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$release = $metadata.releases | Where-Object { $_.sdk.version -notmatch '-' } | Select-Object -First 1
$sdkFile = $release.sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
if ($null -eq $sdkFile -or $sdkFile.hash -notmatch '^[0-9a-fA-F]{128}$') { throw 'SDK metadata is invalid.' }
$downloadUri = [Uri]$sdkFile.url
if ($downloadUri.Scheme -ne 'https' -or $downloadUri.Host -notin @('builds.dotnet.microsoft.com', 'download.visualstudio.microsoft.com')) {
    throw 'Unexpected SDK download host.'
}
$sdkZip = Join-Path $projectWork 'dotnet-sdk.zip'
$sdkDirectory = Join-Path $projectWork 'dotnet'
Invoke-WebRequest -Uri $downloadUri -OutFile $sdkZip
if ((Get-FileHash -LiteralPath $sdkZip -Algorithm SHA512).Hash -ne $sdkFile.hash) { throw 'SDK SHA-512 checksum mismatch.' }
$sdkFile | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $projectWork 'sdk-download.json') -Encoding utf8
Expand-Archive -LiteralPath $sdkZip -DestinationPath $sdkDirectory -Force
& (Join-Path $sdkDirectory 'dotnet.exe') --version
exit $LASTEXITCODE
