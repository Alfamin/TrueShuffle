param(
    [string]$Noctis = "$env:LOCALAPPDATA\Programs\Noctis",
    [string]$Compiler = "$env:USERPROFILE\noctis-lyric-motion\tools\roslyn\tasks\net472\csc.exe",
    [string]$Dotnet = "$env:USERPROFILE\dotnet-sdk\dotnet.exe",
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $out | Out-Null
if (-not (Test-Path -LiteralPath $Compiler)) { throw 'An existing Roslyn compiler is required; no downloads are performed.' }
$kit = Join-Path $Noctis 'Noctis.Plugins.Abstractions.dll'
if (-not (Test-Path -LiteralPath $kit)) { throw 'Noctis plugin kit was not found.' }
$version = [System.Reflection.AssemblyName]::GetAssemblyName($kit).Version
if ($version.Major -ne 1 -or $version.Minor -lt 2) { throw 'Noctis plugin kit 1.2 or later is required.' }
$refs = Get-ChildItem -LiteralPath $Noctis -Filter '*.dll' | Where-Object {
    ($_.Name -like 'System.*' -and $_.Name -notlike '*Native*') -or $_.Name -in @('netstandard.dll','mscorlib.dll','Avalonia.Base.dll','Avalonia.Controls.dll','Noctis.Plugins.Abstractions.dll')
} | ForEach-Object { '-r:"' + $_.FullName + '"' }
$sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { '"' + $_.FullName + '"' }
$rsp = Join-Path $out 'plugin.rsp'
@('-nologo','-nostdlib+','-target:library','-optimize+','-deterministic+','-langversion:latest','-nullable:enable','-warnaserror+',('-out:"' + (Join-Path $out 'TrueShuffle.dll') + '"')) + $refs + $sources | Set-Content -LiteralPath $rsp -Encoding utf8
& $Compiler -noconfig "@$rsp"
if ($LASTEXITCODE -ne 0) { throw 'Plugin compilation failed.' }
Copy-Item -LiteralPath (Join-Path $root 'src\plugin.json') -Destination (Join-Path $out 'plugin.json') -Force
if (-not $SkipTests) {
    $test = Join-Path $out 'tests'
    New-Item -ItemType Directory -Force $test | Out-Null
    $testSources = Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.cs' | ForEach-Object { '"' + $_.FullName + '"' }
    $headless = Join-Path $env:USERPROFILE '.nuget\packages\avalonia.headless\12.1.3\lib\net10.0\Avalonia.Headless.dll'
    if (-not (Test-Path -LiteralPath $headless)) { throw 'Cached Avalonia.Headless 12.1.3 is required for UI tests. No downloads are performed.' }
    $uiRefs = @('-r:"' + $headless + '"') + (@('Avalonia.dll','Avalonia.Themes.Fluent.dll','Avalonia.Skia.dll','Avalonia.HarfBuzz.dll') | ForEach-Object { '-r:"' + (Join-Path $Noctis $_) + '"' })
    $testRsp = Join-Path $test 'test.rsp'
    @('-nologo','-nostdlib+','-target:exe','-optimize+','-deterministic+','-langversion:latest','-nullable:enable','-warnaserror+',('-out:"' + (Join-Path $test 'ShuffleTests.dll') + '"'),('-r:"' + (Join-Path $out 'TrueShuffle.dll') + '"')) + $refs + $uiRefs + $testSources | Set-Content -LiteralPath $testRsp -Encoding utf8
    & $Compiler -noconfig "@$testRsp"
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    Copy-Item -LiteralPath (Join-Path $out 'TrueShuffle.dll') -Destination $test -Force
    Copy-Item -LiteralPath $headless -Destination $test -Force
    foreach ($name in @('Noctis.Plugins.Abstractions.dll','Avalonia.dll','Avalonia.Base.dll','Avalonia.Controls.dll','Avalonia.Markup.dll','Avalonia.Markup.Xaml.dll','Avalonia.Themes.Fluent.dll','Avalonia.Skia.dll','Avalonia.HarfBuzz.dll','SkiaSharp.dll','SkiaSharp.Resources.dll','SkiaSharp.SceneGraph.dll','SkiaSharp.Skottie.dll','libSkiaSharp.dll','HarfBuzzSharp.dll','libHarfBuzzSharp.dll','MicroCom.Runtime.dll')) {
        if (Test-Path -LiteralPath (Join-Path $Noctis $name)) { Copy-Item -LiteralPath (Join-Path $Noctis $name) -Destination $test -Force }
    }
    '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"},"rollForward":"LatestMinor"}}' | Set-Content -LiteralPath (Join-Path $test 'ShuffleTests.runtimeconfig.json') -Encoding utf8
    & $Dotnet (Join-Path $test 'ShuffleTests.dll') $Noctis
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; package was not built.' }
}
$zip = Join-Path $out 'TrueShuffle-for-Noctis.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
Compress-Archive -LiteralPath (Join-Path $out 'TrueShuffle.dll'),(Join-Path $out 'plugin.json') -DestinationPath $zip
"Built $zip"
