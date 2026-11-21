@echo off
rem Project StarCraft - builds KAESA.StarCraft.dll against KSP's own managed assemblies (C# 5, .NET Framework 4 csc).
rem Usage:  build.bat "<KSP root folder>" ["<output dll>"]
rem Needs: KSP 1.12.x and HarmonyKSP (GameData\000_Harmony\0Harmony.dll) in that KSP folder.
rem Every .cs file in this folder is compiled. Default output: bin\KAESA.StarCraft.dll next to this script.
set KSP=%~1
if "%KSP%"=="" (
  echo Usage: build.bat "C:\path\to\Kerbal Space Program" ["output.dll"]
  exit /b 1
)
set OUTDLL=%~2
if "%OUTDLL%"=="" (
  if not exist "%~dp0bin" mkdir "%~dp0bin"
  set OUTDLL=%~dp0bin\KAESA.StarCraft.dll
)
set M=%KSP%\KSP_x64_Data\Managed
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /noconfig /nologo /target:library /optimize+ /nostdlib+ /codepage:65001 ^
  /out:"%OUTDLL%" ^
  /r:"%M%\mscorlib.dll" /r:"%M%\System.dll" /r:"%M%\System.Core.dll" ^
  /r:"%M%\UnityEngine.dll" /r:"%M%\UnityEngine.CoreModule.dll" /r:"%M%\UnityEngine.PhysicsModule.dll" /r:"%M%\UnityEngine.UI.dll" /r:"%M%\UnityEngine.AnimationModule.dll" ^
  /r:"%M%\Assembly-CSharp.dll" /r:"%KSP%\GameData\000_Harmony\0Harmony.dll" ^
  "%~dp0*.cs"
