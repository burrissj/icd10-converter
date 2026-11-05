@echo off
rem Builds the single-file ICD10Converter.exe into the "publish" folder (needs the .NET 10 SDK).
dotnet publish ICD10Converter.csproj -c Release -o publish
if errorlevel 1 (echo Build failed & exit /b 1)
echo.
echo Done: %~dp0publish\ICD10Converter.exe
