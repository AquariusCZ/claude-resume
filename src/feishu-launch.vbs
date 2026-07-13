' Claude Resume - Feishu agent launcher (fully hidden, auto-restart on exit/crash)
Dim sh, fso, here
Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
here = fso.GetParentFolderName(WScript.ScriptFullName)
' Only run if the agent is present and credentials look configured (agent self-exits otherwise).
If Not fso.FileExists(here & "\feishu-agent.js") Then WScript.Quit
Do
  ' rotate the stdout log if it grew large (node is not running here, so deleting is safe)
  On Error Resume Next
  If fso.FileExists(here & "\logs\feishu-stdout.log") Then
    If fso.GetFile(here & "\logs\feishu-stdout.log").Size > 1048576 Then fso.DeleteFile here & "\logs\feishu-stdout.log", True
  End If
  On Error GoTo 0
  ' run node hidden (0) and WAIT (True); capture stdout+stderr (SDK connection logs) to a file;
  ' when it exits, pause then restart -> resilient service
  sh.Run "cmd /c node """ & here & "\feishu-agent.js"" >> """ & here & "\logs\feishu-stdout.log"" 2>&1", 0, True
  WScript.Sleep 8000
Loop
