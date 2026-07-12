' Claude Resume - Feishu agent launcher (fully hidden, auto-restart on exit/crash)
Dim sh, fso, here
Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
here = fso.GetParentFolderName(WScript.ScriptFullName)
' Only run if the agent is present and credentials look configured (agent self-exits otherwise).
If Not fso.FileExists(here & "\feishu-agent.js") Then WScript.Quit
Do
  ' run node hidden (0) and WAIT (True); when it exits, pause then restart -> resilient service
  sh.Run "cmd /c node """ & here & "\feishu-agent.js""", 0, True
  WScript.Sleep 8000
Loop
