Option Explicit

' The production source runs in WSH. Only external side effects are substituted:
' task commands, elevation, WMI, messages and browser launch. MSXML, file IO and
' the startup cmd.exe command use the real Windows implementations.
Dim MockScenario, MockScript, MockExpectedExit, MockNativeFSO, MockNativeShell
Dim MockScriptDir, MockBaseDir, MockSystemRoot, MockSource, MockReader, TestHost
Dim MockRunCount, MockShellCount, MockTaskQueries, MockWmiQueries, MockLastMessage
Dim MockTempXml, MockLogPath, MockSleepCount
MockScenario = WScript.Arguments(0)
MockScript = WScript.Arguments(1)
MockExpectedExit = CInt(WScript.Arguments(2))
Set MockNativeFSO = CreateObject("Scripting.FileSystemObject")
Set MockNativeShell = CreateObject("WScript.Shell")
MockSystemRoot = MockNativeShell.ExpandEnvironmentStrings("%SystemRoot%")
MockScriptDir = MockNativeFSO.GetParentFolderName(MockScript)
MockBaseDir = MockNativeFSO.GetParentFolderName(MockScriptDir)
MockRunCount = 0
MockShellCount = 0
MockTaskQueries = 0
MockWmiQueries = 0
MockLastMessage = ""
MockTempXml = ""
MockSleepCount = 0
Set TestHost = New MockHost
Set MockReader = MockNativeFSO.OpenTextFile(MockScript, 1, False, -1)
MockSource = MockReader.ReadAll
MockReader.Close
MockSource = Replace(MockSource, "CreateObject(", "MockCreateObject(")
MockSource = Replace(MockSource, "GetObject(", "MockGetObject(")
MockSource = Replace(MockSource, "WScript.ScriptFullName", "TestHost.ScriptFullName")
MockSource = Replace(MockSource, "WScript.Arguments", "TestHost.Arguments")
MockSource = Replace(MockSource, "WScript.Quit", "TestHost.Quit")
MockSource = Replace(MockSource, "WScript.Sleep", "TestHost.Sleep")
MockSource = Replace(MockSource, "MsgBox ", "MockMsgBox ")
ExecuteGlobal MockSource
MockVerifyAndQuit 0

Function MockQuote(value)
    MockQuote = Chr(34) & value & Chr(34)
End Function

Sub MockCheck(condition, message)
    If Not condition Then
        WScript.Echo "FAIL:" & MockScenario & ":" & message
        WScript.Quit 97
    End If
End Sub

Sub MockMsgBox(message, style, title)
    MockLastMessage = message
End Sub

Sub MockVerifyAndQuit(code)
    MockCheck code = MockExpectedExit, "unexpected script exit"
    If InStr(MockScenario, "elevate") > 0 Or MockScenario = "enable_cancel" Then
        MockCheck MockShellCount = 1, "elevation not requested exactly once"
        MockCheck MockRunCount = 0, "command ran before elevation"
        MockCheck MockTaskQueries = 0, "task queried before elevation"
    ElseIf Left(MockScenario, 7) = "enable_" Then
        MockCheck MockShellCount = 0, "elevated script relaunched itself"
        MockCheck Len(MockTempXml) > 0, "task XML was not submitted"
        MockCheck Not MockNativeFSO.FileExists(MockTempXml), "temporary XML leaked"
        If MockScenario = "enable_create_fail" Then
            MockCheck MockRunCount = 1, "failed creation should stop immediately"
        Else
            MockCheck MockRunCount = 2, "creation must be followed by verification"
        End If
        If code = 0 Then MockCheck InStr(MockLastMessage, "已注册或更新") > 0, "missing registration success"
    ElseIf Left(MockScenario, 8) = "disable_" Then
        If MockScenario = "disable_missing" Or MockScenario = "disable_access_denied" Or MockScenario = "disable_connect_fail" Then
            MockCheck MockRunCount = 0, "deletion ran after unsuccessful lookup"
        Else
            MockCheck MockRunCount = 1, "must delete only the intended task"
            If MockScenario <> "disable_delete_fail" Then MockCheck MockTaskQueries = 2, "missing deletion verification"
        End If
        If MockScenario = "disable_ok" Then
            MockCheck InStr(MockLastMessage, "已删除") > 0, "missing deletion success"
        Else
            MockCheck InStr(MockLastMessage, "已删除") = 0, "false deletion success"
        End If
    ElseIf Left(MockScenario, 6) = "delay_" Then
        MockCheck MockWmiQueries = 0, "startup must not inspect or terminate WMI processes"
        If MockScenario = "delay_missing" Then
            MockCheck MockRunCount = 0 And MockSleepCount = 0, "missing launcher should fail before waiting or running"
        Else
            MockCheck MockRunCount = 1 And MockSleepCount = 1, "launcher must run exactly once after the delay"
        End If
    ElseIf MockScenario = "startup_ok" Then
        MockCheck MockShellCount = 1, "core was not launched"
        MockCheck MockWmiQueries = 2, "startup was not checked"
        Dim stream, output
        Set stream = CreateObject("ADODB.Stream")
        stream.Type = 2
        stream.Charset = "utf-8"
        stream.Open
        stream.LoadFromFile MockLogPath
        output = stream.ReadText
        stream.Close
        MockCheck InStr(output, "ARGC=2") > 0, "wrong core argument count"
        MockCheck InStr(output, "ARG0=-d") > 0, "missing config switch"
        MockCheck InStr(output, "ARG1=" & MockBaseDir & "\config") > 0, "config path was split or changed"
    End If
    WScript.Echo "PASS:" & MockScenario
    WScript.Quit code
End Sub

Function MockCreateObject(progId)
    Select Case progId
        Case "WScript.Shell": Set MockCreateObject = New MockShell
        Case "Shell.Application": Set MockCreateObject = New MockApplication
        Case "Scripting.FileSystemObject", "Msxml2.DOMDocument.6.0"
            Set MockCreateObject = CreateObject(progId)
        Case "Schedule.Service": Set MockCreateObject = New MockScheduler
        Case Else: MockCheck False, "unexpected COM object: " & progId
    End Select
End Function

Function MockGetObject(path)
    MockCheck Left(MockScenario, 6) <> "delay_", "delayed startup must not depend on WMI"
    Set MockGetObject = New MockWmi
End Function

Class MockHost
    Public Property Get ScriptFullName
        ScriptFullName = MockScript
    End Property
    Public Property Get Arguments
        Set Arguments = New MockArguments
    End Property
    Public Sub Sleep(milliseconds)
        If Left(MockScenario, 6) = "delay_" Then
            MockSleepCount = MockSleepCount + 1
            MockCheck milliseconds = 10000, "startup delay changed unexpectedly"
        End If
    End Sub
    Public Sub Quit(code)
        MockVerifyAndQuit code
    End Sub
End Class

Class MockArguments
    Public Property Get Named
        Set Named = New MockNamedArguments
    End Property
End Class

Class MockNamedArguments
    Public Function Exists(name)
        Exists = InStr(MockScenario, "elevate") = 0 And MockScenario <> "enable_cancel"
    End Function
End Class

Class MockApplication
    Public Sub ShellExecute(executable, arguments, workingDirectory, verb, windowStyle)
        MockShellCount = MockShellCount + 1
        MockCheck verb = "runas", "missing runas verb"
        If MockScenario = "startup_ok" Then
            MockCheck executable = "cmd.exe", "unexpected core launcher"
            MockCheck Left(arguments, 12) = "/d /s /c " & Chr(34) & Chr(34) & Left(MockBaseDir, 1), "missing executable quotes"
            Dim regex, matches, exitCode
            Set regex = New RegExp
            regex.Pattern = " > ""([^""]+)"""
            Set matches = regex.Execute(arguments)
            MockCheck matches.Count = 1, "missing log redirection"
            MockLogPath = matches(0).SubMatches(0)
            exitCode = MockNativeShell.Run(MockQuote(MockSystemRoot & "\System32\cmd.exe") & " " & arguments, 0, True)
            MockCheck exitCode = 0, "native startup command failed"
        Else
            MockCheck executable = MockSystemRoot & "\System32\wscript.exe", "unexpected elevated host"
            MockCheck arguments = MockQuote(MockScript) & " /elevated", "elevation script path was not quoted"
            MockCheck workingDirectory = MockScriptDir, "wrong elevation working directory"
            If MockScenario = "enable_cancel" Then Err.Raise &H800704C7, "Mock", "UAC cancelled"
        End If
    End Sub
End Class

Class MockShell
    Public Function ExpandEnvironmentStrings(value)
        ExpandEnvironmentStrings = MockNativeShell.ExpandEnvironmentStrings(value)
    End Function
    Public Function Run(command, windowStyle, wait)
        MockRunCount = MockRunCount + 1
        Run = 0
        If Left(MockScenario, 6) = "delay_" Then
            MockCheck command = MockQuote(MockSystemRoot & "\System32\wscript.exe") & " //B //NoLogo " & MockQuote(MockScriptDir & "\Surfing.tray.vbs") & " /start", "wrong delayed startup command or quoting"
            MockCheck windowStyle = 0 And wait, "launcher must be hidden and allowed to finish"
            If MockScenario = "delay_start_fail" Then Err.Raise &H80070002, "Mock", "Cannot start script host"
            If MockScenario = "delay_launcher_fail" Then Run = 7
            Exit Function
        End If
        If InStr(command, " /Create ") > 0 Then
            Dim regex, matches, document
            Set regex = New RegExp
            regex.Pattern = " /XML ""([^""]+)"""
            Set matches = regex.Execute(command)
            MockCheck matches.Count = 1, "missing quoted XML path"
            MockTempXml = matches(0).SubMatches(0)
            Set document = CreateObject("Msxml2.DOMDocument.6.0")
            MockCheck document.Load(MockTempXml), "MSXML could not read generated XML"
            document.setProperty "SelectionNamespaces", "xmlns:t='http://schemas.microsoft.com/windows/2004/02/mit/task'"
            MockCheck document.selectSingleNode("//t:Arguments").text = MockQuote(MockScriptDir & "\Surfing.delay.vbs"), "wrong task arguments"
            MockCheck document.selectSingleNode("//t:WorkingDirectory").text = MockScriptDir, "wrong task working directory"
            MockCheck document.selectSingleNode("//t:Command").text = MockSystemRoot & "\System32\wscript.exe", "wrong task executable"
            MockCheck document.selectSingleNode("//t:StartWhenAvailable").text = "true", "wrong missed-schedule policy"
            MockCheck document.selectSingleNode("//t:RunOnlyIfNetworkAvailable").text = "false", "startup still depends on network availability"
            MockCheck document.selectSingleNode("//t:RestartOnFailure/t:Interval").text = "PT1M", "wrong startup retry interval"
            MockCheck document.selectSingleNode("//t:RestartOnFailure/t:Count").text = "3", "wrong startup retry count"
            MockCheck InStr(document.xml, "&amp;") > 0, "path ampersand was not XML-escaped"
            MockCheck Right(command, 3) = " /F", "existing tasks would not be updated"
            If MockScenario = "enable_create_fail" Then Run = 5
        ElseIf InStr(command, " /Query ") > 0 Then
            If MockScenario = "enable_query_fail" Then Run = 1
        ElseIf InStr(command, " /Delete ") > 0 Then
            MockCheck command = MockQuote(MockSystemRoot & "\System32\schtasks.exe") & " /Delete /TN " & MockQuote("\Surfing\Surfing.startup") & " /F", "wrong deletion target"
            If MockScenario = "disable_delete_fail" Then Run = 5
        Else
            MockCheck MockScenario = "startup_ok", "unexpected command"
        End If
    End Function
End Class

Class MockScheduler
    Public Sub Connect
        If MockScenario = "disable_connect_fail" Then Err.Raise &H800706BA, "Mock", "Service unavailable"
    End Sub
    Public Function GetFolder(path)
        Set GetFolder = New MockRootFolder
    End Function
End Class

Class MockRootFolder
    Public Function GetTask(name)
        MockTaskQueries = MockTaskQueries + 1
        MockCheck name = "\Surfing\Surfing.startup", "wrong lookup target"
        If MockScenario = "disable_missing" Or (MockScenario = "disable_ok" And MockTaskQueries = 2) Then
            Err.Raise &H80070002, "Mock", "Task not found"
        ElseIf MockScenario = "disable_access_denied" Then
            Err.Raise &H80070005, "Mock", "Access denied"
        ElseIf MockScenario = "disable_verify_fail" And MockTaskQueries = 2 Then
            Err.Raise &H800706BA, "Mock", "Service unavailable"
        Else
            Set GetTask = New MockTask
        End If
    End Function
End Class

Class MockTask
End Class

Class MockWmi
    Public Function ExecQuery(query)
        Dim processes
        Set processes = New MockProcesses
        MockWmiQueries = MockWmiQueries + 1
        processes.Count = MockWmiQueries - 1
        Set ExecQuery = processes
    End Function
End Class

Class MockProcesses
    Public Count
End Class
