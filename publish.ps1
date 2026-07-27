param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "MacroFenetre.csproj"
$outputPath = Join-Path $PSScriptRoot "dist"

dotnet publish $projectPath --configuration Release --runtime $Runtime --self-contained true --output $outputPath -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false

Write-Host "Version autonome créée dans : $outputPath"
