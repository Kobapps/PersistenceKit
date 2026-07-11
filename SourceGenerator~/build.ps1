# Builds the source generator and drops PersistenceKit.SourceGenerator.dll into
# ../Runtime/Plugins/ so Unity picks it up alongside the Runtime asmdef.
#
# After the first build you must select the generated DLL in the Project window and
# enable the "RoslynAnalyzer" asset label so Unity treats it as an analyzer rather than
# a runtime assembly. The accompanying .meta file already declares the label, but if
# you delete it Unity will regenerate it without the analyzer flag.

$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

dotnet build PersistenceKit.SourceGenerator.csproj -c Release

Write-Host "Source generator built. Refresh Unity (Ctrl+R) to pick up the new analyzer DLL." -ForegroundColor Green
