param(
    [Parameter(Mandatory=$true)][string] $UnityEditorPath,
    [Parameter(Mandatory=$true)][string] $ReferenceResponseFile,
    [Parameter(Mandatory=$true)][string] $OutputDirectory,
    [switch] $IncludeValidation
)
$ErrorActionPreference = 'Stop'
$editorFolder = (Split-Path -Parent (Resolve-Path -LiteralPath $UnityEditorPath).Path).Replace('\','/') + '/'
$runtime = Join-Path $editorFolder 'Data/NetCoreRuntime/dotnet.exe'
$compiler = Join-Path $editorFolder 'Data/DotNetSdkRoslyn/csc.dll'
$original = Get-Content -LiteralPath $ReferenceResponseFile
# Reuse only the installed Unity/framework references and defines. Never run the
# project's custom analyzers or write its original assembly output paths.
$references = @($original | Where-Object {
    $_ -match '^-r:"([^\"]+)"$' -and
    $Matches[1].Replace('\','/').StartsWith($editorFolder, [StringComparison]::OrdinalIgnoreCase)
})
if ($references.Count -eq 0) { throw 'No installed Unity/framework references found.' }
$defines = @($original | Where-Object { $_.StartsWith('-define:') })
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../Assets/ResourceOrganizer/Editor') -Filter '*.cs' | ForEach-Object { $_.FullName })
if ($IncludeValidation) {
    $sources += @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'UnityEditorValidation*.cs' | ForEach-Object { $_.FullName })
    $sources += (Join-Path $PSScriptRoot 'Fixtures/FR2_Cache.cs')
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path
$response = Join-Path $outputRoot 'unity-compile.rsp'
$options = @('-target:library', '-nostdlib+', '-langversion:9.0', '-nologo', '-warn:4',
    ('-out:"' + (Join-Path $outputRoot 'Suyao.ResourceOrganizer.Editor.dll') + '"'))
$options += $references
$options += $defines
$options += @($sources | ForEach-Object { '"' + $_ + '"' })
Set-Content -LiteralPath $response -Value $options -Encoding utf8
& $runtime $compiler ('@' + $response)
if ($LASTEXITCODE -ne 0) { throw "Unity reference compilation failed: $LASTEXITCODE" }
Write-Output "Unity reference compilation passed: $($sources.Count) source files, $($references.Count) installed references."
