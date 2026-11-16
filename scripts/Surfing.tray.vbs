Option Explicit

Dim FSO, Shell, ScriptDir, BaseDir, TrayPath, Arguments
Set FSO = CreateObject("Scripting.FileSystemObject")
Set Shell = CreateObject("Shell.Application")
ScriptDir = FSO.GetParentFolderName(WScript.ScriptFullName)
BaseDir = FSO.GetParentFolderName(ScriptDir)
TrayPath = FSO.BuildPath(BaseDir, "bin\SurfingTray.exe")
Arguments = ""

If WScript.Arguments.Named.Exists("start") Then Arguments = "--start"
If WScript.Arguments.Named.Exists("stop") Then Arguments = "--stop"
If WScript.Arguments.Named.Exists("restart") Then Arguments = "--restart"

If Not FSO.FileExists(TrayPath) Then
    MsgBox "SurfingTray.exe is missing. Please download the complete release or run scripts\build-tray.ps1.", vbCritical, "SurfingWin"
    WScript.Quit 1
End If

On Error Resume Next
Shell.ShellExecute TrayPath, Arguments, BaseDir, "open", 0
If Err.Number <> 0 Then
    MsgBox "Cannot open the tray manager: " & Err.Description, vbCritical, "SurfingWin"
    WScript.Quit 1
End If
On Error GoTo 0
