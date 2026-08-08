' Claude Resume - GUI launcher (hides the PowerShell console; the WPF window still shows)
Dim sh, fso, here
Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
here = fso.GetParentFolderName(WScript.ScriptFullName)
sh.Run "powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -STA -File """ & here & "\picker.ps1""", 0, False
