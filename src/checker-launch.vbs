' Claude Resume - checker launcher for the Scheduled Task (fully hidden, no window)
Dim sh, fso, here
Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
here = fso.GetParentFolderName(WScript.ScriptFullName)
sh.Run "powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File """ & here & "\checker.ps1""", 0, False
