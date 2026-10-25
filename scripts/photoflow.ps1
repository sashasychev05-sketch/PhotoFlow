param(
    [Parameter(Position = 0)][string]$Action = 'help',
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$ProbeArguments
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectWork = [IO.Path]::GetFullPath((Join-Path $repoRoot '..\work'))
$portableDotnet = Join-Path $projectWork 'dotnet\dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $portableDotnet) { $portableDotnet } else { 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $projectWork 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectWork 'nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:MSBUILDDISABLENODEREUSE = '1'
Push-Location -LiteralPath $repoRoot
try {
    switch ($Action) {
        'restore' { & $dotnetExe restore PhotoFlow.slnx --locked-mode --source 'https://api.nuget.org/v3/index.json' }
        'build' { & $dotnetExe build PhotoFlow.slnx --configuration Release --no-restore -m:1 }
        'app' {
            & $dotnetExe build src/PhotoFlow.App/PhotoFlow.App.csproj --configuration Release --no-restore -m:1 -v quiet
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            & $dotnetExe (Join-Path $repoRoot 'src\PhotoFlow.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\PhotoFlow.App.dll')
        }
        'test' {
            & $dotnetExe build tests/PhotoFlow.Tests/PhotoFlow.Tests.csproj --configuration Release --no-restore -m:1 -v quiet
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            & $dotnetExe (Join-Path $repoRoot 'tests\PhotoFlow.Tests\bin\Release\net10.0\PhotoFlow.Tests.dll')
        }
        default {
            & $dotnetExe build src/PhotoFlow.Probe/PhotoFlow.Probe.csproj --configuration Release --no-restore -m:1 -v quiet
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            & $dotnetExe (Join-Path $repoRoot 'src\PhotoFlow.Probe\bin\Release\net10.0\PhotoFlow.Probe.dll') $Action @ProbeArguments
        }
    }
    exit $LASTEXITCODE
}
finally { Pop-Location }
