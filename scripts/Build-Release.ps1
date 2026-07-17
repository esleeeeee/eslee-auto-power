param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet = Join-Path $root '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
}

$artifacts = Join-Path $root 'artifacts'
$publishRoot = Join-Path $artifacts 'publish'
$installerOutput = Join-Path $artifacts 'installer'
foreach ($path in @($publishRoot, $installerOutput)) {
    $full = [IO.Path]::GetFullPath($path)
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the repository: $full"
    }
    if (Test-Path -LiteralPath $full) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
    New-Item -ItemType Directory -Path $full -Force | Out-Null
}

$solution = Join-Path $root 'AutoPower.sln'
& $dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed: $LASTEXITCODE" }

$projects = @(
    'src\AutoPower.App\AutoPower.App.csproj',
    'src\AutoPower.Helper\AutoPower.Helper.csproj',
    'src\AutoPower.Agent\AutoPower.Agent.csproj'
)
$languages = @('ko', 'en')
foreach ($language in $languages) {
    & $dotnet build $solution -c Release --no-restore -p:Version=$Version -p:AppLanguage=$language
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for ${language}: $LASTEXITCODE" }

    & $dotnet test (Join-Path $root 'tests\AutoPower.Tests\AutoPower.Tests.csproj') `
        -c Release --no-build -p:Version=$Version -p:AppLanguage=$language
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed for ${language}: $LASTEXITCODE" }

    $publish = Join-Path $publishRoot "$language\win-x64"
    New-Item -ItemType Directory -Path $publish -Force | Out-Null
    foreach ($project in $projects) {
        & $dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true `
            -o $publish -p:Version=$Version -p:AppLanguage=$language `
            -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for ${project} (${language}): $LASTEXITCODE" }
    }
}

$isccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($null -eq $isccCommand) {
    $defaultIscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    $userIscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
    if (Test-Path -LiteralPath $defaultIscc) {
        $isccPath = $defaultIscc
    } elseif (Test-Path -LiteralPath $userIscc) {
        $isccPath = $userIscc
    } else {
        throw 'Inno Setup 6 ISCC.exe was not found.'
    }
} else {
    $isccPath = $isccCommand.Source
}

$hashes = foreach ($language in $languages) {
    & $isccPath "/DMyAppVersion=$Version" "/DMyAppLanguage=$language" `
        (Join-Path $root 'installer\AutoPower.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed for ${language}: $LASTEXITCODE" }

    $setup = Join-Path $installerOutput "eslee-auto-power-v$Version-$language-setup.exe"
    if (-not (Test-Path -LiteralPath $setup)) {
        throw "Installer was not created: $setup"
    }

    $setupHash = Get-FileHash -Algorithm SHA256 -LiteralPath $setup
    $hashLine = "$($setupHash.Hash)  $([IO.Path]::GetFileName($setup))$([Environment]::NewLine)"
    [IO.File]::WriteAllText("$setup.sha256", $hashLine, [Text.UTF8Encoding]::new($false))
    $setupHash
}

$hashes
