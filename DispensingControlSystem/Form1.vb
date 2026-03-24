Imports EasyModbus
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading.Tasks
Imports System.Drawing
Imports System.Windows.Forms
Imports System.Drawing.Drawing2D
Imports System.Linq

''' <summary>
''' VBX INDUSTRIAL DISPENSING CONTROL SYSTEM - PRODUCTION v3.0
''' Premium HMI with Robust Hardware Integration and Industrial-Grade Design.
''' </summary>
Partial Class Form1
    Inherits Form

#Region "Hardware Configuration"
    ' === Defaults per actual hardware (user images) ===
    Private Const DEFAULT_MODBUS_IP As String = "192.168.1.12"
    Private Const DEFAULT_MODBUS_PORT As Integer = 502
    Private Const DEFAULT_COGNEX_IP As String = "192.168.1.20"
    Private Const DEFAULT_COGNEX_PORT As Integer = 80
    Private Const DEFAULT_KEYENCE_IP As String = "192.168.1.54"
    Private Const DEFAULT_KEYENCE_PORT As Integer = 23

    Public Class AppConfig
        Public Property ModbusIP As String = DEFAULT_MODBUS_IP
        Public Property ModbusPort As Integer = DEFAULT_MODBUS_PORT
        Public Property CognexIP As String = DEFAULT_COGNEX_IP
        Public Property CognexPort As Integer = DEFAULT_COGNEX_PORT
        Public Property KeyenceIP As String = DEFAULT_KEYENCE_IP
        Public Property KeyencePort As Integer = DEFAULT_KEYENCE_PORT
        Public Property PassCount As Integer = 0
        Public Property FailCount As Integer = 0
        Public Property CameraUrl As String = "http://192.168.1.20/img/snapshot.jpg"
        Public Property CameraSourcePath As String = ""  ' FTP or local file path (leave empty = use CameraUrl HTTP)
        Public Property MasterBarcode As String = "VBX-001"
        Public Property DebugLogEnabled As Boolean = True
        Public Property ProgramNames As String() = Enumerable.Range(1, 15).Select(Function(i) $"PROGRAM {i:D2}").ToArray()
    End Class

    Private config As New AppConfig()
    Private ReadOnly configPath As String = IO.Path.Combine(Application.StartupPath, "settings.config.json")
    Private ReadOnly logDir As String = IO.Path.Combine(Application.StartupPath, "logs")
    Private logWriter As IO.StreamWriter
    Private cycleStartTime As DateTime
#End Region

#Region "I/O Mapping (per Hardware I/O Table — user images)"
    ' ── INPUTS (Discrete Inputs from Modbus) ──
    Private Const DI_ESTOP As Integer = 0        ' I0.0  Emergency Stop (NC)
    Private Const DI_START As Integer = 1         ' I0.1  Start Button
    Private Const DI_START2 As Integer = 2        ' I0.2  Start Button (2)
    Private Const DI_CURTAIN As Integer = 3       ' I0.3  Safety Light Curtain KEYENCE (NC)
    Private Const DI_ROBOT_RUN As Integer = 4     ' I0.4  Robot Running
    Private Const DI_ROBOT_DONE As Integer = 5    ' I0.5  Robot Complete
    Private Const DI_ROBOT_FAULT As Integer = 6   ' I0.6  Robot Fault
    Private Const DI_VISION_OK As Integer = 7     ' I0.7  Vision OK (Cognex)
    Private Const DI_VISION_NG As Integer = 8     ' I1.0  Vision NG (Cognex)
    Private Const DI_CYL_EXT As Integer = 9       ' I1.1  Cylinder Extend Sensor 1
    Private Const DI_CYL_EXT2 As Integer = 10     ' I1.2  Cylinder Extend Sensor 2
    Private Const DI_CYL_RET As Integer = 11      ' I1.3  Cylinder Retract Sensor 1
    Private Const DI_CYL_RET2 As Integer = 12     ' I1.4  Cylinder Retract Sensor 2

    ' ── OUTPUTS (Coils to Modbus) ──
    Private Const DO_LIGHT_RED As Integer = 0     ' Q0.0  Tower Light Red
    Private Const DO_LIGHT_YEL As Integer = 1     ' Q0.1  Tower Light Yellow
    Private Const DO_LIGHT_GRN As Integer = 2     ' Q0.2  Tower Light Green
    ' Q0.3 reserved
    Private Const DO_ROBOT_ESTOP As Integer = 4   ' Q0.4  Robot Emergency Signal
    Private Const DO_ROBOT_START As Integer = 5   ' Q0.5  Robot Start Signal (500ms pulse)
    Private Const DO_ROBOT_PAUSE As Integer = 6   ' Q0.6  Robot Pause Signal
    Private Const DO_PROG_LOAD As Integer = 7     ' Q0.7  Program Number LOAD
    Private Const DO_PROG_BIT0 As Integer = 8     ' Q1.0  Program bit0 (2^0=1)
    Private Const DO_PROG_BIT1 As Integer = 9     ' Q1.1  Program bit1 (2^1=2)
    Private Const DO_PROG_BIT2 As Integer = 10    ' Q1.2  Program bit2 (2^2=4)
    Private Const DO_PROG_BIT3 As Integer = 11    ' Q1.3  Program bit3 (2^3=8)
    Private Const DO_CLAMP As Integer = 12        ' Q1.4  Cylinder Clamp 1
    Private Const DO_CLAMP2 As Integer = 13       ' Q1.5  Cylinder Clamp 2
    Private Const DO_CLAMP3 As Integer = 14       ' Q1.6  Cylinder Clamp 3
    Private Const DO_CLAMP4 As Integer = 15       ' Q1.7  Cylinder Clamp 4
#End Region

#Region "Machine State System"
    Public Enum MachineStatus
        IDLE                ' Green ON — wait for Start
        CLAMP_EXTEND        ' Cylinder clamp — wait extend sensor
        SCANNING            ' Yellow ON — barcode scan
        MODEL_CHECK         ' Barcode match decision
        MODEL_FAIL          ' Red ON — wrong PWBA popup
        MODEL_FAIL_RETRACT  ' Cylinder retract after wrong model
        CURTAIN_CHECK       ' Verify safety curtain
        DISPENSE_START      ' Send program bits + robot start
        DISPENSE_RUNNING    ' Wait robot complete — yellow blink
        DISPENSE_DONE       ' Yellow OFF — green blink
        VISION_CHECK        ' Trigger vision inspection
        VISION_OK_RETRACT   ' Unclamp + retract after PASS
        VISION_NG           ' Red ON — NG error
        VISION_NG_RETRACT   ' Retract after NG
        CYCLE_COMPLETE      ' Green ON — send data — finish
        FAULT_ALARM         ' Generic fault
        EMERGENCY_STOP      ' E-Stop active
    End Enum

    Private currentState As MachineStatus = MachineStatus.IDLE
    Private inputs(15) As Boolean
    Private outputs(15) As Boolean
    Private isEStopActive As Boolean = False
    Private isPaused As Boolean = False
    Private isSoftwareStartRequested As Boolean = False
    Private isSoftwareResetRequested As Boolean = False
    Private cycleDuration As Double = 0
    Private modbusClient As ModbusClient
    Private lastBarcode As String = "N/A"
    Private lastVisionResult As String = "N/A"
    Private alarmMessage As String = ""
#End Region

#Region "Premium UI Components"
    ' Production Theme Colors
    Private ReadOnly CLR_BG As Color = Color.FromArgb(12, 12, 14)
    Private ReadOnly CLR_PANEL As Color = Color.FromArgb(22, 22, 26)
    Private ReadOnly CLR_CARD As Color = Color.FromArgb(30, 30, 36)
    Private ReadOnly CLR_ACCENT As Color = Color.FromArgb(0, 120, 255)
    Private ReadOnly CLR_ACCENT2 As Color = Color.FromArgb(0, 200, 120)
    Private ReadOnly CLR_TEXT As Color = Color.FromArgb(240, 240, 245)
    Private ReadOnly CLR_DIM As Color = Color.FromArgb(120, 120, 130)
    Private ReadOnly CLR_DANGER As Color = Color.FromArgb(255, 59, 48)
    Private ReadOnly CLR_WARN As Color = Color.FromArgb(255, 149, 0)
    Private ReadOnly CLR_PASS As Color = Color.FromArgb(52, 199, 89)
    Private ReadOnly CLR_BORDER As Color = Color.FromArgb(44, 44, 50)

    ' Layout
    Private lblTitle, lblClock, lblStateText, lblModelDisplay, lblAlarmDetail As Label
    Private pnlHeader, pnlSideBar, pnlFooter As Panel
    Private picCameraPreview As PictureBox
    Private pnlCameraOverlay As Panel
    Private lblCameraStatus, lblVisionOverlay As Label
    Private btnStart, btnReset, btnPause, btnConfig, btnExit As Button
    Private indModbus, indVision, indScanner As Panel
    Private lblIndModbus, lblIndVision, lblIndScanner As Label
    Private logDisplay As ListBox
    Private mainTimer, cameraTimer As Timer
    Private lblStatsPass, lblStatsFail, lblStatsTime, lblStatsYield As Label
    Private cbProgramSelect As ComboBox
    Private isModbusLive, isVisionLive, isScannerLive, isProcessing, isNetWorking As Boolean
    Private Shared httpClient As New System.Net.Http.HttpClient() With {.Timeout = TimeSpan.FromSeconds(5)}
    Private modbusReconnectCooldown As Integer = 0
    Private cameraNoSignalBmp As Bitmap
    Private logoCached As Image
    Private animPulse As Integer = 0
    Private pnlIOBar As Panel
    Private diLeds(15) As Panel
    Private doLeds(15) As Panel
#End Region

#Region "Core Machine Logic — ViscoTec Flowchart"
    Private Async Function RunWorkflowAsync() As Task
        Dim triggerStart = isSoftwareStartRequested OrElse inputs(DI_START) OrElse inputs(DI_START2)

        Dim triggerReset = isSoftwareResetRequested
        isSoftwareStartRequested = False
        isSoftwareResetRequested = False

        ' ── Global Safety: E-Stop (NC — must be HIGH) ──
        If Not inputs(DI_ESTOP) Then
            currentState = MachineStatus.EMERGENCY_STOP
            isEStopActive = True
            alarmMessage = "EMERGENCY STOP — I0.0 Safety Circuit Open"
        End If

        If triggerReset Then
            currentState = MachineStatus.IDLE
            isEStopActive = False
            alarmMessage = ""
            ResetOutputs()
            Log("SYSTEM", "Manual Reset Initiated")
            Return
        End If

        If currentState = MachineStatus.EMERGENCY_STOP Then
            outputs(DO_LIGHT_RED) = True
            outputs(DO_ROBOT_ESTOP) = True   ' DO-03 Robot emergency
            outputs(DO_LIGHT_GRN) = False
            outputs(DO_LIGHT_YEL) = False
            Return
        End If

        Select Case currentState
            ' ── IDLE: Green ON, wait Start ──
            Case MachineStatus.IDLE
                ResetOutputs()
                outputs(DO_LIGHT_GRN) = True        ' Green = Ready
                If triggerStart Then
                    cycleStartTime = DateTime.Now
                    ' Activate cylinder clamp
                    outputs(DO_CLAMP) = True
                    outputs(DO_CLAMP2) = True
                    outputs(DO_CLAMP3) = True
                    outputs(DO_CLAMP4) = True
                    Log("CYCLE", "▶ Start — Clamp Cylinder Extend")
                    currentState = MachineStatus.CLAMP_EXTEND
                End If

            ' ── CLAMP_EXTEND: Wait cylinder extend sensor ──
            Case MachineStatus.CLAMP_EXTEND
                outputs(DO_LIGHT_GRN) = False
                If inputs(DI_CYL_EXT) OrElse inputs(DI_CYL_EXT2) Then
                    outputs(DO_LIGHT_YEL) = True     ' Yellow = Busy
                    Log("CLAMP", "✓ Cylinder Extended — Scanning Barcode")
                    currentState = MachineStatus.SCANNING
                End If

            ' ── SCANNING: Barcode scan via Keyence Ethernet ──
            Case MachineStatus.SCANNING
                Log("SCAN", "Acquiring Barcode...")
                Dim barcode = Await ReadBarcodeAsync()
                If Not String.IsNullOrEmpty(barcode) AndAlso barcode <> "ERROR" AndAlso barcode <> "ER" AndAlso barcode <> "TIMEOUT" Then
                    lastBarcode = barcode
                    isScannerLive = True
                    Log("SCAN", $"Barcode: {barcode}")
                    currentState = MachineStatus.MODEL_CHECK
                Else
                    isScannerLive = False
                    Log("SCAN", "Read Failed — Retrying...")
                    Await Task.Delay(500)
                End If

            ' ── MODEL_CHECK: Match barcode ──
            Case MachineStatus.MODEL_CHECK
                If lastBarcode = config.MasterBarcode OrElse config.MasterBarcode = "*" Then
                    Log("VERIFY", $"✓ Model Match: {lastBarcode}")
                    currentState = MachineStatus.CURTAIN_CHECK
                Else
                    alarmMessage = $"Wrong PWBA! Expected: {config.MasterBarcode}, Got: {lastBarcode}"
                    Log("VERIFY", $"✗ {alarmMessage}")
                    currentState = MachineStatus.MODEL_FAIL
                End If

            ' ── MODEL_FAIL: Red ON, popup wrong PWBA ──
            Case MachineStatus.MODEL_FAIL
                outputs(DO_LIGHT_RED) = True
                outputs(DO_LIGHT_YEL) = False
                outputs(DO_LIGHT_GRN) = False
                ' Wait for operator press Start to acknowledge
                If triggerStart Then
                    Log("SYSTEM", "Operator acknowledged — Retracting")
                    outputs(DO_CLAMP) = False
                    outputs(DO_CLAMP2) = False
                    outputs(DO_CLAMP3) = False
                    outputs(DO_CLAMP4) = False
                    currentState = MachineStatus.MODEL_FAIL_RETRACT
                End If

            ' ── MODEL_FAIL_RETRACT: Wait retract then IDLE ──
            Case MachineStatus.MODEL_FAIL_RETRACT
                If inputs(DI_CYL_RET) OrElse inputs(DI_CYL_RET2) Then
                    outputs(DO_LIGHT_RED) = False
                    outputs(DO_LIGHT_GRN) = True
                    alarmMessage = ""
                    Log("CLAMP", "✓ Retracted — Remove Part")
                    config.FailCount += 1 : SaveSettings()
                    currentState = MachineStatus.IDLE
                End If

            ' ── CURTAIN_CHECK: Safety curtain must be active ──
            Case MachineStatus.CURTAIN_CHECK
                If inputs(DI_CURTAIN) Then
                    Log("SAFETY", "✓ Light Curtain Active")
                    currentState = MachineStatus.DISPENSE_START
                Else
                    alarmMessage = "Safety Light Curtain NOT Active (I0.3)"
                    Log("SAFETY", alarmMessage)
                    currentState = MachineStatus.FAULT_ALARM
                End If

            ' ── DISPENSE_START: Send program + robot start ──
            Case MachineStatus.DISPENSE_START
                outputs(DO_LIGHT_YEL) = True
                UpdateProgramBits()
                outputs(DO_PROG_LOAD) = True         ' Q0.7 LOAD
                Await Task.Delay(200)
                outputs(DO_ROBOT_START) = True       ' Q0.5 Robot Start pulse
                Await Task.Delay(500)
                outputs(DO_ROBOT_START) = False
                Log("ROBOT", $"▶ Dispensing Program {cbProgramSelect.SelectedIndex + 1} Started")
                currentState = MachineStatus.DISPENSE_RUNNING

            ' ── DISPENSE_RUNNING: Wait robot complete ──
            Case MachineStatus.DISPENSE_RUNNING
                ' Safety: check curtain during dispensing
                If Not inputs(DI_CURTAIN) Then
                    alarmMessage = "Light Curtain Interrupted During Dispensing!"
                    Log("SAFETY", alarmMessage)
                    outputs(DO_ROBOT_PAUSE) = True    ' Q0.6 Pause robot
                    currentState = MachineStatus.EMERGENCY_STOP
                    Return
                End If
                ' Yellow blink effect
                outputs(DO_LIGHT_YEL) = (animPulse Mod 4 < 2)
                If inputs(DI_ROBOT_DONE) Then         ' I0.5 Robot Complete
                    Log("ROBOT", "✓ Dispensing Complete")
                    outputs(DO_LIGHT_YEL) = False
                    outputs(DO_PROG_LOAD) = False
                    currentState = MachineStatus.DISPENSE_DONE
                ElseIf inputs(DI_ROBOT_FAULT) Then    ' I0.6 Robot Fault
                    alarmMessage = "Robot Fault Signal (I0.6)"
                    Log("FAULT", alarmMessage)
                    currentState = MachineStatus.FAULT_ALARM
                End If

            ' ── DISPENSE_DONE: Green blink then vision ──
            Case MachineStatus.DISPENSE_DONE
                outputs(DO_LIGHT_GRN) = (animPulse Mod 4 < 2)
                Await Task.Delay(800)
                currentState = MachineStatus.VISION_CHECK

            ' ── VISION_CHECK: Trigger Cognex inspection ──
            Case MachineStatus.VISION_CHECK
                Log("VISION", "Triggering Cognex Inspection...")
                Dim result = Await SendTcpHandshakeAsync(config.CognexIP, config.CognexPort, "T" & vbCr)
                lastVisionResult = result
                ' Check I/O signals + TCP response
                Dim isOK = inputs(DI_VISION_OK) OrElse result.ToUpper().Contains("OK") OrElse result.Contains("1")
                Dim isNG = inputs(DI_VISION_NG)
                If isOK AndAlso Not isNG Then
                    Log("VISION", "✓ Inspection PASSED")
                    lastVisionResult = "PASS"
                    config.PassCount += 1 : SaveSettings()
                    ' Auto unclamp + retract
                    outputs(DO_CLAMP) = False
                    outputs(DO_CLAMP2) = False
                    outputs(DO_CLAMP3) = False
                    outputs(DO_CLAMP4) = False
                    currentState = MachineStatus.VISION_OK_RETRACT
                Else
                    Log("VISION", "✗ Inspection FAILED")
                    lastVisionResult = "FAIL"
                    config.FailCount += 1 : SaveSettings()
                    currentState = MachineStatus.VISION_NG
                End If

            ' ── VISION_OK_RETRACT: Wait retract → complete ──
            Case MachineStatus.VISION_OK_RETRACT
                If inputs(DI_CYL_RET) OrElse inputs(DI_CYL_RET2) Then
                    outputs(DO_LIGHT_GRN) = True
                    cycleDuration = (DateTime.Now - cycleStartTime).TotalSeconds
                    Log("CYCLE", $"✓ Cycle Complete ({cycleDuration:F2}s)")
                    currentState = MachineStatus.CYCLE_COMPLETE
                End If

            ' ── VISION_NG: Red ON, wait acknowledge ──
            Case MachineStatus.VISION_NG
                outputs(DO_LIGHT_RED) = True
                outputs(DO_LIGHT_GRN) = False
                alarmMessage = "Vision Inspection FAILED — NG"
                If triggerStart Then
                    Log("SYSTEM", "Operator acknowledged NG — Retracting")
                    outputs(DO_CLAMP) = False
                    outputs(DO_CLAMP2) = False
                    outputs(DO_CLAMP3) = False
                    outputs(DO_CLAMP4) = False
                    currentState = MachineStatus.VISION_NG_RETRACT
                End If

            ' ── VISION_NG_RETRACT: Retract then IDLE ──
            Case MachineStatus.VISION_NG_RETRACT
                If inputs(DI_CYL_RET) OrElse inputs(DI_CYL_RET2) Then
                    outputs(DO_LIGHT_RED) = False
                    outputs(DO_LIGHT_GRN) = True
                    alarmMessage = ""
                    Log("CLAMP", "✓ Retracted — Remove NG Part")
                    currentState = MachineStatus.IDLE
                End If

            ' ── CYCLE_COMPLETE: Green ON → IDLE ──
            Case MachineStatus.CYCLE_COMPLETE
                outputs(DO_LIGHT_GRN) = True
                Log("DATA", "Sending data to server...")
                ' TODO: server data upload here
                Await Task.Delay(500)
                currentState = MachineStatus.IDLE

            ' ── FAULT_ALARM: Generic fault ──
            Case MachineStatus.FAULT_ALARM
                outputs(DO_LIGHT_RED) = True
                outputs(DO_LIGHT_YEL) = False
                outputs(DO_LIGHT_GRN) = False
                If triggerReset Then
                    alarmMessage = ""
                    currentState = MachineStatus.IDLE
                End If
        End Select
    End Function
#End Region

#Region "Hardware Handlers"
    Private Async Function ReadBarcodeAsync() As Task(Of String)
        DebugLog($"SCAN: Connecting to {config.KeyenceIP}:{config.KeyencePort}")
        Try
            Using client As New TcpClient()
                client.ReceiveTimeout = 5000
                client.SendTimeout = 3000
                Dim connectTask = client.ConnectAsync(config.KeyenceIP, config.KeyencePort)
                If Await Task.WhenAny(connectTask, Task.Delay(3000)) IsNot connectTask Then
                    DebugLog("SCAN: Connection timeout")
                    Return "TIMEOUT"
                End If
                Await connectTask
                DebugLog("SCAN: TCP connected")
                isScannerLive = True

                Using stream = client.GetStream()
                    ' Try LON command with CR+LF (some Keyence models need this)
                    Dim cmd = Encoding.ASCII.GetBytes("LON" & vbCrLf)
                    Await stream.WriteAsync(cmd, 0, cmd.Length)
                    DebugLog("SCAN: Sent LON command")

                    ' Wait for scanner to acquire and respond
                    Await Task.Delay(500)

                    ' Read with extended attempts
                    Dim fullResponse As String = ""
                    Dim buffer(4096) As Byte
                    Dim attempts = 0
                    Dim maxAttempts = 10

                    While attempts < maxAttempts
                        If stream.DataAvailable Then
                            Dim bytesRead = Await stream.ReadAsync(buffer, 0, buffer.Length)
                            If bytesRead > 0 Then
                                fullResponse &= Encoding.ASCII.GetString(buffer, 0, bytesRead)
                                DebugLog($"SCAN: Read {bytesRead} bytes: [{fullResponse.Replace(vbCr, "\r").Replace(vbLf, "\n")}]")
                            End If
                            ' Check if we got a complete response
                            If fullResponse.Contains(vbCr) OrElse fullResponse.Contains(vbLf) OrElse fullResponse.Length > 3 Then
                                Exit While
                            End If
                        Else
                            Await Task.Delay(300)
                        End If
                        attempts += 1
                    End While

                    ' Send LOFF to stop scanner
                    cmd = Encoding.ASCII.GetBytes("LOFF" & vbCrLf)
                    Await stream.WriteAsync(cmd, 0, cmd.Length)
                    DebugLog("SCAN: Sent LOFF")

                    ' Clean up response
                    Dim result = fullResponse.Trim()
                    result = result.Replace(vbCr, "").Replace(vbLf, "")
                    ' Strip echo if present
                    If result.StartsWith("LON", StringComparison.OrdinalIgnoreCase) Then
                        result = result.Substring(3).Trim()
                    End If
                    ' Remove any control chars
                    result = New String(result.Where(Function(c) Not Char.IsControl(c)).ToArray()).Trim()

                    DebugLog($"SCAN: Final barcode = [{result}] (len={result.Length})")

                    If String.IsNullOrWhiteSpace(result) Then
                        DebugLog("SCAN: Empty response after cleanup")
                        Return "ERROR"
                    End If
                    Return result
                End Using
            End Using
        Catch ex As Exception
            DebugLog($"SCAN-ERR: {ex.GetType().Name}: {ex.Message}")
            Log("SCAN", $"Scanner Error: {ex.Message}")
            isScannerLive = False
            Return "ERROR"
        End Try
    End Function

    Private Async Function SendTcpHandshakeAsync(ip As String, port As Integer, cmd As String) As Task(Of String)
        Try
            Using client As New TcpClient()
                Dim ct = client.ConnectAsync(ip, port)
                If Await Task.WhenAny(ct, Task.Delay(1500)) Is ct Then
                    Using s = client.GetStream()
                        Dim d = Encoding.ASCII.GetBytes(cmd)
                        Await s.WriteAsync(d, 0, d.Length)
                        Dim b(1024) As Byte
                        Dim resTask = s.ReadAsync(b, 0, b.Length)
                        If Await Task.WhenAny(resTask, Task.Delay(3000)) Is resTask Then
                            Dim n = Await resTask
                            Return Encoding.ASCII.GetString(b, 0, n).Trim()
                        End If
                    End Using
                End If
            End Using
        Catch ex As Exception
            Log("TCP", $"Connection Error: {ex.Message}")
            Return "NET_ERR"
        End Try
        Return "TIMEOUT"
    End Function

    Private Sub ResetOutputs()
        For i = 0 To 15 : outputs(i) = False : Next
    End Sub

    Private Sub UpdateProgramBits()
        Dim progNum = cbProgramSelect.SelectedIndex + 1
        outputs(DO_PROG_BIT0) = (progNum And 1) <> 0   ' Q1.0
        outputs(DO_PROG_BIT1) = (progNum And 2) <> 0   ' Q1.1
        outputs(DO_PROG_BIT2) = (progNum And 4) <> 0   ' Q1.2
        outputs(DO_PROG_BIT3) = (progNum And 8) <> 0   ' Q1.3
    End Sub

    ''' <summary>
    ''' Modbus sync with proper async reconnection and backoff
    ''' </summary>
    Private Async Sub SyncModbusAsync(sender As Object, e As EventArgs)
        If isProcessing Then Return
        isProcessing = True
        Try
            If modbusClient Is Nothing OrElse Not modbusClient.Connected Then
                isModbusLive = False
                If modbusReconnectCooldown > 0 Then
                    modbusReconnectCooldown -= 1
                ElseIf Not isNetWorking Then
                    isNetWorking = True
                    Try
                        Await Task.Run(Sub()
                            modbusClient = New ModbusClient(config.ModbusIP, config.ModbusPort)
                            modbusClient.ConnectionTimeout = 2000
                            modbusClient.Connect()
                        End Sub)
                        If modbusClient.Connected Then
                            isModbusLive = True
                            Log("MODBUS", $"✓ Connected to {config.ModbusIP}:{config.ModbusPort}")
                        End If
                    Catch ex As Exception
                        modbusClient = Nothing
                        modbusReconnectCooldown = 50 ' ~5 seconds backoff
                        Log("MODBUS", $"Connection failed — retry in 5s")
                    Finally
                        isNetWorking = False
                    End Try
                End If
            End If

            If modbusClient IsNot Nothing AndAlso modbusClient.Connected Then
                isModbusLive = True
                Dim di = Await Task.Run(Function() modbusClient.ReadDiscreteInputs(0, 16))
                For i = 0 To 15 : inputs(i) = di(i) : Next
                If Not isPaused Then Await RunWorkflowAsync()
                Await Task.Run(Sub() modbusClient.WriteMultipleCoils(0, outputs))
            End If

            UpdateHMI()
        Catch ex As Exception
            isModbusLive = False
            modbusClient = Nothing
            modbusReconnectCooldown = 30
        Finally
            isProcessing = False
        End Try
    End Sub

    ''' <summary>
    ''' Camera frame update — supports HTTP, FTP, and local file paths
    ''' </summary>
    Private Async Sub UpdateCameraFrameAsync(sender As Object, e As EventArgs)
        If picCameraPreview Is Nothing OrElse isPaused Then Return
        Try
            Dim bmp As Bitmap = Nothing
            Dim source = If(Not String.IsNullOrWhiteSpace(config.CameraSourcePath), config.CameraSourcePath, config.CameraUrl)

            If source.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) Then
                bmp = Await Task.Run(Function() FetchImageFromFtp(source))
            ElseIf source.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) Then
                bmp = Await Task.Run(Function() FetchImageFromTcp(source))
            ElseIf source.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then
                ' Use WebClient (HTTP/1.0 compatible — Cognex embedded server rejects HttpClient HTTP/1.1)
                Dim urls As New List(Of String) From {source}
                Dim baseUrl = $"http://{config.CognexIP}"
                If Not urls.Contains($"{baseUrl}/img/snapshot.jpg") Then urls.Add($"{baseUrl}/img/snapshot.jpg")
                If Not urls.Contains($"{baseUrl}/CgiImage?img=snapshot.bmp") Then urls.Add($"{baseUrl}/CgiImage?img=snapshot.bmp")
                If Not urls.Contains($"{baseUrl}/CgiSnapshot") Then urls.Add($"{baseUrl}/CgiSnapshot")
                If Not urls.Contains($"{baseUrl}/liveimg.cgi?imgformat=jpg") Then urls.Add($"{baseUrl}/liveimg.cgi?imgformat=jpg")
                If Not urls.Contains($"{baseUrl}/api/image/snapshot") Then urls.Add($"{baseUrl}/api/image/snapshot")
                If Not urls.Contains($"{baseUrl}/snapshot.jpg") Then urls.Add($"{baseUrl}/snapshot.jpg")
                If Not urls.Contains($"{baseUrl}/image.jpg") Then urls.Add($"{baseUrl}/image.jpg")

                For Each url In urls
                    Try
                        Dim data As Byte() = Nothing
                        Await Task.Run(Sub()
                            Using wc As New System.Net.WebClient()
                                wc.Headers.Add("User-Agent", "VBX/3.0")
                                data = wc.DownloadData(url)
                            End Using
                        End Sub)
                        If data IsNot Nothing AndAlso data.Length > 100 Then
                            Using ms As New IO.MemoryStream(data)
                                bmp = New Bitmap(ms)
                            End Using
                            If url <> config.CameraUrl Then
                                config.CameraUrl = url
                                SaveSettings()
                                Log("CAMERA", $"Auto-detected working URL: {url}")
                            End If
                            Exit For
                        End If
                    Catch urlEx As Exception
                        DebugLog($"CAM-TRY: {url} -> {urlEx.Message}")
                    End Try
                Next
            ElseIf IO.File.Exists(source) Then
                bmp = Await Task.Run(Function()
                    Using fs = New IO.FileStream(source, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.ReadWrite)
                        Return New Bitmap(fs)
                    End Using
                End Function)
            Else
                Throw New Exception($"Invalid camera source: {source}")
            End If

            If bmp IsNot Nothing Then
                picCameraPreview.Invoke(Sub()
                    If picCameraPreview.Image IsNot Nothing AndAlso picCameraPreview.Image IsNot cameraNoSignalBmp Then
                        picCameraPreview.Image.Dispose()
                    End If
                    picCameraPreview.Image = bmp
                End Sub)
                isVisionLive = True
                UpdateCameraOverlay(True)
            Else
                Throw New Exception("No image data received")
            End If
        Catch ex As Exception
            DebugLog($"CAM-ERR: {ex.Message}")
            isVisionLive = False
            Try
                picCameraPreview.Invoke(Sub()
                    If picCameraPreview.Image Is Nothing OrElse picCameraPreview.Image Is cameraNoSignalBmp Then Return
                    picCameraPreview.Image = cameraNoSignalBmp
                End Sub)
            Catch : End Try
            UpdateCameraOverlay(False)
        End Try
    End Sub

    Private Sub UpdateCameraOverlay(isLive As Boolean)
        Try
            lblCameraStatus.Invoke(Sub()
                If isLive Then
                    lblCameraStatus.Text = " ● LIVE"
                    lblCameraStatus.BackColor = Color.FromArgb(180, 0, 180, 80)
                    lblCameraStatus.ForeColor = Color.White
                Else
                    lblCameraStatus.Text = " ○ NO SIGNAL"
                    lblCameraStatus.BackColor = Color.FromArgb(180, 180, 0, 0)
                    lblCameraStatus.ForeColor = Color.White
                End If

                If lastVisionResult = "PASS" Then
                    lblVisionOverlay.Text = "PASS"
                    lblVisionOverlay.ForeColor = CLR_PASS
                    lblVisionOverlay.Visible = True
                ElseIf lastVisionResult = "FAIL" Then
                    lblVisionOverlay.Text = "FAIL"
                    lblVisionOverlay.ForeColor = CLR_DANGER
                    lblVisionOverlay.Visible = True
                Else
                    lblVisionOverlay.Visible = False
                End If
            End Sub)
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' Fetch image from FTP URL (ftp://user:pass@host/path/image.jpg)
    ''' </summary>
    Private Function FetchImageFromFtp(ftpUrl As String) As Bitmap
        Dim request = CType(System.Net.WebRequest.Create(ftpUrl), System.Net.FtpWebRequest)
        request.Method = System.Net.WebRequestMethods.Ftp.DownloadFile
        request.UseBinary = True
        request.Timeout = 3000
        ' Extract credentials from URL if present (ftp://user:pass@host/path)
        Try
            Dim uri As New Uri(ftpUrl)
            If Not String.IsNullOrEmpty(uri.UserInfo) Then
                Dim parts = uri.UserInfo.Split(":"c)
                If parts.Length = 2 Then
                    request.Credentials = New System.Net.NetworkCredential(parts(0), parts(1))
                End If
            End If
        Catch
        End Try
        Using response = CType(request.GetResponse(), System.Net.FtpWebResponse)
            Using stream = response.GetResponseStream()
                Return New Bitmap(stream)
            End Using
        End Using
    End Function

    ''' <summary>
    ''' Fetch image via TCP (tcp://ip:port) - connects, sends T\r trigger, reads image stream
    ''' </summary>
    Private Function FetchImageFromTcp(tcpUrl As String) As Bitmap
        Dim uri As New Uri(tcpUrl.Replace("tcp://", "http://"))
        Dim host = uri.Host
        Dim port = If(uri.Port > 0, uri.Port, 23)
        Using client As New TcpClient()
            client.Connect(host, port)
            Using stream = client.GetStream()
                Dim cmd = Encoding.ASCII.GetBytes("T" & vbCr)
                stream.Write(cmd, 0, cmd.Length)
                stream.Flush()
                Threading.Thread.Sleep(500)
                Using ms As New IO.MemoryStream()
                    Dim buffer(8192) As Byte
                    Dim totalRead = 0
                    While stream.DataAvailable OrElse totalRead = 0
                        Dim n = stream.Read(buffer, 0, buffer.Length)
                        If n = 0 Then Exit While
                        ms.Write(buffer, 0, n)
                        totalRead += n
                        If Not stream.DataAvailable Then Threading.Thread.Sleep(100)
                    End While
                    If totalRead > 100 Then
                        ms.Position = 0
                        Return New Bitmap(ms)
                    End If
                End Using
            End Using
        End Using
        Return Nothing
    End Function

    ''' <summary>
    ''' Auto-detect and probe all devices on startup
    ''' </summary>
    Private Async Function AutoDetectDevicesAsync() As Task
        DebugLog("AUTO-DETECT: Starting device scan...")
        Log("DETECT", $"Scanning Modbus at {config.ModbusIP}:{config.ModbusPort}")

        ' === 1. Probe Modbus TCP ===
        Try
            Await Task.Run(Sub()
                modbusClient = New ModbusClient(config.ModbusIP, config.ModbusPort)
                modbusClient.ConnectionTimeout = 3000
                modbusClient.Connect()
            End Sub)
            If modbusClient IsNot Nothing AndAlso modbusClient.Connected Then
                isModbusLive = True
                Log("DETECT", $"✓ Modbus ONLINE at {config.ModbusIP}:{config.ModbusPort}")
                DebugLog("AUTO-DETECT: Modbus connected OK")
            End If
        Catch ex As Exception
            isModbusLive = False
            modbusClient = Nothing
            Log("DETECT", $"✗ Modbus OFFLINE ({ex.Message})")
            DebugLog($"AUTO-DETECT: Modbus failed: {ex.Message}")
        End Try

        ' === 2. Probe Cognex Vision TCP (send T\r) ===
        Log("DETECT", $"Scanning Cognex at {config.CognexIP}:{config.CognexPort}")
        Try
            Dim result = Await SendTcpHandshakeAsync(config.CognexIP, config.CognexPort, "T" & vbCr)
            If result <> "TIMEOUT" AndAlso result <> "NET_ERR" Then
                isVisionLive = True
                Log("DETECT", $"✓ Cognex ONLINE (response: {result})")
            Else
                isVisionLive = False
                Log("DETECT", $"✗ Cognex OFFLINE ({result})")
            End If
        Catch ex As Exception
            isVisionLive = False
            Log("DETECT", $"✗ Cognex OFFLINE ({ex.Message})")
        End Try

        ' === 3. Probe Keyence Scanner TCP ===
        Log("DETECT", $"Scanning Keyence at {config.KeyenceIP}:{config.KeyencePort}")
        Try
            Using client As New TcpClient()
                Dim ct = client.ConnectAsync(config.KeyenceIP, config.KeyencePort)
                If Await Task.WhenAny(ct, Task.Delay(2000)) Is ct Then
                    Await ct
                    isScannerLive = True
                    Log("DETECT", $"✓ Keyence ONLINE at {config.KeyenceIP}:{config.KeyencePort}")
                    DebugLog("AUTO-DETECT: Keyence TCP connected OK")
                Else
                    isScannerLive = False
                    Log("DETECT", "✗ Keyence OFFLINE (timeout)")
                End If
            End Using
        Catch ex As Exception
            isScannerLive = False
            Log("DETECT", $"✗ Keyence OFFLINE ({ex.Message})")
        End Try

        ' === 4. Probe Camera HTTP ===
        Log("DETECT", $"Scanning Camera at {config.CameraUrl}")
        Try
            Dim resp = Await httpClient.GetAsync(config.CameraUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead)
            If resp.IsSuccessStatusCode Then
                Log("DETECT", "✓ Camera HTTP ONLINE")
            Else
                Log("DETECT", $"✗ Camera HTTP error ({resp.StatusCode})")
            End If
        Catch ex As Exception
            Log("DETECT", $"✗ Camera HTTP OFFLINE ({ex.Message})")
        End Try

        DebugLog("AUTO-DETECT: Scan complete")
        Log("DETECT", "Device scan complete")
        UpdateHMI()
    End Function
#End Region

#Region "Debug Logging"
    ''' <summary>
    ''' Initialize debug log file — creates logs/ directory and daily log file
    ''' </summary>
    Private Sub InitDebugLog()
        Try
            If Not IO.Directory.Exists(logDir) Then IO.Directory.CreateDirectory(logDir)
            Dim logFile = IO.Path.Combine(logDir, $"debug_{DateTime.Now:yyyy-MM-dd}.log")
            logWriter = New IO.StreamWriter(logFile, True, Encoding.UTF8) With {.AutoFlush = True}
            logWriter.WriteLine($"")
            logWriter.WriteLine($"═══════════════════════════════════════════════════")
            logWriter.WriteLine($"  VBX DEBUG LOG — Started {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            logWriter.WriteLine($"═══════════════════════════════════════════════════")
            logWriter.WriteLine($"  Modbus: {config.ModbusIP}:{config.ModbusPort}")
            logWriter.WriteLine($"  Cognex: {config.CognexIP}:{config.CognexPort}")
            logWriter.WriteLine($"  Scanner: {config.KeyenceIP}:{config.KeyencePort}")
            logWriter.WriteLine($"  Camera URL: {config.CameraUrl}")
            logWriter.WriteLine($"  Camera Path: {If(String.IsNullOrEmpty(config.CameraSourcePath), "(none — using HTTP)", config.CameraSourcePath)}")
            logWriter.WriteLine($"═══════════════════════════════════════════════════")
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' Write a debug entry to the log file (if enabled)
    ''' </summary>
    Private Sub DebugLog(msg As String)
        If Not config.DebugLogEnabled Then Return
        Try
            logWriter?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}")
        Catch
        End Try
    End Sub
#End Region

#Region "HMI Initialization"
    Public Sub New()
        InitializeComponent()
        Me.Text = "VBX DISPENSING CONTROL v3.0"
        Me.BackColor = CLR_BG
        Me.DoubleBuffered = True
        Me.FormBorderStyle = FormBorderStyle.None
        Me.WindowState = FormWindowState.Maximized

        LoadSettings()
        InitDebugLog()
        CreateNoSignalBitmap()
        LoadLogo()
        BuildLayout()

        ' Auto-detect devices on startup
        AddHandler Me.Shown, Async Sub()
            Log("SYSTEM", "Auto-detecting devices...")
            Await AutoDetectDevicesAsync()
        End Sub

        mainTimer = New Timer With {.Interval = 100}
        AddHandler mainTimer.Tick, AddressOf SyncModbusAsync
        mainTimer.Start()

        cameraTimer = New Timer With {.Interval = 800}
        AddHandler cameraTimer.Tick, AddressOf UpdateCameraFrameAsync
        cameraTimer.Start()

        Log("SYSTEM", "═══ VBX Production HMI v3.0 Initialized ═══")
        Log("SYSTEM", $"Modbus: {config.ModbusIP} | Camera: {config.CognexIP} | Scanner: {config.KeyenceIP}")
    End Sub

    Private Sub CreateNoSignalBitmap()
        cameraNoSignalBmp = New Bitmap(640, 480)
        Using g = Graphics.FromImage(cameraNoSignalBmp)
            g.Clear(Color.FromArgb(18, 18, 22))
            g.SmoothingMode = SmoothingMode.AntiAlias
            ' Crosshair
            Using p As New Pen(Color.FromArgb(60, 255, 255, 255), 1)
                p.DashStyle = DashStyle.Dash
                g.DrawLine(p, 320, 0, 320, 480)
                g.DrawLine(p, 0, 240, 640, 240)
                g.DrawEllipse(p, 220, 140, 200, 200)
            End Using
            ' Text
            Using sf As New StringFormat With {.Alignment = StringAlignment.Center, .LineAlignment = StringAlignment.Center}
                g.DrawString("NO SIGNAL", New Font("Segoe UI", 28, FontStyle.Bold), New SolidBrush(Color.FromArgb(100, 255, 60, 60)), New RectangleF(0, 180, 640, 60), sf)
                g.DrawString("Camera feed unavailable", New Font("Segoe UI", 12), New SolidBrush(Color.FromArgb(80, 255, 255, 255)), New RectangleF(0, 250, 640, 30), sf)
            End Using
        End Using
    End Sub

    Private Sub LoadLogo()
        Try
            Dim logoPath = IO.Path.Combine(Application.StartupPath, "logo.jpg")
            If IO.File.Exists(logoPath) Then logoCached = Image.FromFile(logoPath)
        Catch : End Try
    End Sub

    Private Sub BuildLayout()
        Dim tlp As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 2, .RowCount = 4, .Margin = New Padding(0), .Padding = New Padding(0)}
        tlp.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 74))
        tlp.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 26))
        tlp.RowStyles.Add(New RowStyle(SizeType.Absolute, 80))   ' Header
        tlp.RowStyles.Add(New RowStyle(SizeType.Percent, 100))   ' Main
        tlp.RowStyles.Add(New RowStyle(SizeType.Absolute, 56))   ' I/O Bar
        tlp.RowStyles.Add(New RowStyle(SizeType.Absolute, 180))  ' Footer
        Me.Controls.Add(tlp)

        BuildHeader(tlp)
        BuildCameraArea(tlp)
        BuildSidebar(tlp)
        BuildIOBar(tlp)
        BuildFooter(tlp)
    End Sub

    Private Sub BuildHeader(tlp As TableLayoutPanel)
        pnlHeader = New Panel With {.Dock = DockStyle.Fill, .BackColor = CLR_PANEL, .Margin = New Padding(0)}
        AddHandler pnlHeader.Paint, Sub(s, e)
            Using brush As New LinearGradientBrush(pnlHeader.ClientRectangle, Color.FromArgb(20, 22, 28), Color.FromArgb(28, 30, 40), 0.0F)
                e.Graphics.FillRectangle(brush, pnlHeader.ClientRectangle)
            End Using
            Using p As New Pen(CLR_ACCENT, 2)
                e.Graphics.DrawLine(p, 0, pnlHeader.Height - 1, pnlHeader.Width, pnlHeader.Height - 1)
            End Using
        End Sub
        tlp.Controls.Add(pnlHeader, 0, 0) : tlp.SetColumnSpan(pnlHeader, 2)

        ' Logo
        If logoCached IsNot Nothing Then
            Dim picLogo As New PictureBox With {.Image = logoCached, .SizeMode = PictureBoxSizeMode.Zoom, .Size = New Size(55, 55), .Location = New Point(16, 12), .BackColor = Color.Transparent}
            pnlHeader.Controls.Add(picLogo)
        End If

        lblTitle = New Label With {
            .Text = "VBX DISPENSING MACHINE", .ForeColor = CLR_ACCENT,
            .Font = New Font("Segoe UI", 20, FontStyle.Bold), .AutoSize = True,
            .Location = New Point(80, 8), .BackColor = Color.Transparent
        }
        Dim lblSubTitle As New Label With {
            .Text = "PRODUCTION CONTROL SYSTEM", .ForeColor = CLR_DIM,
            .Font = New Font("Segoe UI", 9), .AutoSize = True,
            .Location = New Point(82, 48), .BackColor = Color.Transparent
        }

        lblStateText = New Label With {
            .Text = "SYSTEM IDLE", .ForeColor = CLR_PASS,
            .Font = New Font("Segoe UI", 18, FontStyle.Bold), .TextAlign = ContentAlignment.MiddleCenter,
            .Size = New Size(380, 52), .Anchor = AnchorStyles.None, .BackColor = Color.FromArgb(36, 38, 44)
        }
        AddHandler pnlHeader.Resize, Sub()
            lblStateText.Location = New Point((pnlHeader.Width - lblStateText.Width) \ 2, 14)
        End Sub

        lblClock = New Label With {
            .Text = "00:00:00", .ForeColor = CLR_TEXT, .BackColor = Color.Transparent,
            .Font = New Font("Consolas", 16, FontStyle.Bold), .AutoSize = True,
            .Anchor = AnchorStyles.Top Or AnchorStyles.Right
        }
        Dim lblDate As New Label With {
            .Text = DateTime.Now.ToString("dd MMM yyyy"), .ForeColor = CLR_DIM, .BackColor = Color.Transparent,
            .Font = New Font("Segoe UI", 9), .AutoSize = True,
            .Anchor = AnchorStyles.Top Or AnchorStyles.Right
        }
        AddHandler pnlHeader.Resize, Sub()
            lblClock.Location = New Point(pnlHeader.Width - 185, 12)
            lblDate.Location = New Point(pnlHeader.Width - 185, 46)
        End Sub

        pnlHeader.Controls.AddRange({lblTitle, lblSubTitle, lblStateText, lblClock, lblDate})
    End Sub

    Private Sub BuildCameraArea(tlp As TableLayoutPanel)
        Dim pnlCam As New Panel With {.Dock = DockStyle.Fill, .Padding = New Padding(8, 8, 4, 4), .BackColor = CLR_BG}
        tlp.Controls.Add(pnlCam, 0, 1)

        Dim pnlCamBorder As New Panel With {.Dock = DockStyle.Fill, .BackColor = CLR_CARD, .Padding = New Padding(2)}
        pnlCam.Controls.Add(pnlCamBorder)

        picCameraPreview = New PictureBox With {
            .Dock = DockStyle.Fill, .BackColor = Color.FromArgb(14, 14, 18),
            .SizeMode = PictureBoxSizeMode.Zoom, .Image = cameraNoSignalBmp
        }
        pnlCamBorder.Controls.Add(picCameraPreview)

        ' Camera crosshair overlay via Paint event
        AddHandler picCameraPreview.Paint, Sub(s, e)
            Dim g = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias
            Dim cx = picCameraPreview.Width \ 2
            Dim cy = picCameraPreview.Height \ 2
            Using p As New Pen(Color.FromArgb(50, 0, 200, 255), 1)
                p.DashStyle = DashStyle.Dot
                g.DrawLine(p, cx, 0, cx, picCameraPreview.Height)
                g.DrawLine(p, 0, cy, picCameraPreview.Width, cy)
            End Using
            ' Corner brackets
            Dim sz = 30
            Using p As New Pen(Color.FromArgb(100, 0, 200, 255), 2)
                g.DrawLines(p, {New Point(20, 20 + sz), New Point(20, 20), New Point(20 + sz, 20)})
                g.DrawLines(p, {New Point(picCameraPreview.Width - 20 - sz, 20), New Point(picCameraPreview.Width - 20, 20), New Point(picCameraPreview.Width - 20, 20 + sz)})
                g.DrawLines(p, {New Point(20, picCameraPreview.Height - 20 - sz), New Point(20, picCameraPreview.Height - 20), New Point(20 + sz, picCameraPreview.Height - 20)})
                g.DrawLines(p, {New Point(picCameraPreview.Width - 20 - sz, picCameraPreview.Height - 20), New Point(picCameraPreview.Width - 20, picCameraPreview.Height - 20), New Point(picCameraPreview.Width - 20, picCameraPreview.Height - 20 - sz)})
            End Using
        End Sub

        ' Floating overlays
        lblCameraStatus = New Label With {
            .Text = " ○ NO SIGNAL", .AutoSize = True, .BackColor = Color.FromArgb(180, 180, 0, 0),
            .ForeColor = Color.White, .Font = New Font("Segoe UI Semibold", 10),
            .Location = New Point(10, 10), .Padding = New Padding(6, 3, 6, 3)
        }
        picCameraPreview.Controls.Add(lblCameraStatus)

        lblVisionOverlay = New Label With {
            .Text = "", .AutoSize = True, .BackColor = Color.Transparent,
            .Font = New Font("Segoe UI", 36, FontStyle.Bold), .Visible = False,
            .Anchor = AnchorStyles.Bottom Or AnchorStyles.Right
        }
        AddHandler picCameraPreview.Resize, Sub()
            lblVisionOverlay.Location = New Point(picCameraPreview.Width - 200, picCameraPreview.Height - 80)
        End Sub
        picCameraPreview.Controls.Add(lblVisionOverlay)
    End Sub

    Private Sub BuildSidebar(tlp As TableLayoutPanel)
        pnlSideBar = New Panel With {.Dock = DockStyle.Fill, .BackColor = CLR_PANEL, .Padding = New Padding(12, 12, 12, 8)}
        AddHandler pnlSideBar.Paint, Sub(s, e)
            Using p As New Pen(CLR_BORDER, 1) : e.Graphics.DrawLine(p, 0, 0, 0, pnlSideBar.Height) : End Using
        End Sub
        tlp.Controls.Add(pnlSideBar, 1, 1) : tlp.SetRowSpan(pnlSideBar, 2)

        Dim flp As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .FlowDirection = FlowDirection.TopDown, .WrapContents = False, .AutoScroll = True}

        ' Model/Barcode Card
        lblModelDisplay = New Label With {
            .Text = "NO MODEL", .ForeColor = Color.White, .BackColor = CLR_CARD,
            .Font = New Font("Consolas", 16, FontStyle.Bold), .TextAlign = ContentAlignment.MiddleCenter,
            .Size = New Size(280, 48), .Margin = New Padding(0, 0, 0, 12)
        }
        AddPaintBorder(lblModelDisplay, CLR_ACCENT)
        flp.Controls.Add(lblModelDisplay)

        ' Alarm Display
        lblAlarmDetail = New Label With {
            .Text = "", .ForeColor = CLR_DANGER, .BackColor = Color.FromArgb(40, 255, 59, 48),
            .Font = New Font("Segoe UI", 9), .TextAlign = ContentAlignment.MiddleLeft,
            .Size = New Size(280, 0), .Margin = New Padding(0, 0, 0, 8), .Padding = New Padding(8, 4, 4, 4)
        }
        flp.Controls.Add(lblAlarmDetail)

        ' Stats Cards
        flp.Controls.Add(CreateStatCard("PASS COUNT", CLR_PASS, lblStatsPass))
        flp.Controls.Add(CreateStatCard("FAIL COUNT", CLR_DANGER, lblStatsFail))
        flp.Controls.Add(CreateStatCard("CYCLE TIME", CLR_WARN, lblStatsTime))
        flp.Controls.Add(CreateStatCard("YIELD %", CLR_ACCENT, lblStatsYield))

        ' Separator
        flp.Controls.Add(New Label With {.Text = "CONNECTIONS", .ForeColor = CLR_DIM, .Font = New Font("Segoe UI", 8, FontStyle.Bold), .AutoSize = True, .Margin = New Padding(0, 14, 0, 6)})

        ' Connection Indicators
        indModbus = CreateIndicatorPanel("MODBUS TCP", lblIndModbus) : flp.Controls.Add(indModbus)
        indVision = CreateIndicatorPanel("COGNEX VISION", lblIndVision) : flp.Controls.Add(indVision)
        indScanner = CreateIndicatorPanel("KEYENCE SCAN", lblIndScanner) : flp.Controls.Add(indScanner)

        ' Program Selector
        flp.Controls.Add(New Label With {.Text = "PROGRAM SELECT", .ForeColor = CLR_DIM, .Font = New Font("Segoe UI", 8, FontStyle.Bold), .AutoSize = True, .Margin = New Padding(0, 14, 0, 6)})
        cbProgramSelect = New ComboBox With {
            .Width = 280, .Font = New Font("Consolas", 11),
            .BackColor = CLR_CARD, .ForeColor = CLR_TEXT,
            .DropDownStyle = ComboBoxStyle.DropDownList, .FlatStyle = FlatStyle.Flat
        }
        cbProgramSelect.Items.AddRange(config.ProgramNames)
        cbProgramSelect.SelectedIndex = 0
        flp.Controls.Add(cbProgramSelect)

        pnlSideBar.Controls.Add(flp)
    End Sub

    Private Function CreateStatCard(title As String, accent As Color, ByRef valLabel As Label) As Panel
        Dim card As New Panel With {.Size = New Size(280, 58), .BackColor = CLR_CARD, .Margin = New Padding(0, 0, 0, 6)}
        AddPaintBorder(card, Color.FromArgb(60, accent.R, accent.G, accent.B))

        Dim lbl As New Label With {.Text = title, .ForeColor = CLR_DIM, .Font = New Font("Segoe UI", 8), .Location = New Point(12, 6), .AutoSize = True, .BackColor = Color.Transparent}
        valLabel = New Label With {.Text = "0", .ForeColor = accent, .Font = New Font("Segoe UI", 20, FontStyle.Bold), .Location = New Point(12, 22), .AutoSize = True, .BackColor = Color.Transparent}
        card.Controls.AddRange({lbl, valLabel})
        Return card
    End Function

    Private Function CreateIndicatorPanel(txt As String, ByRef statusLbl As Label) As Panel
        Dim p As New Panel With {.Size = New Size(280, 32), .BackColor = Color.Transparent, .Margin = New Padding(0, 2, 0, 2)}
        Dim dot As New Panel With {.Size = New Size(10, 10), .Location = New Point(4, 11), .BackColor = CLR_DANGER}
        MakeCircle(dot)
        Dim lbl As New Label With {.Text = txt, .ForeColor = CLR_TEXT, .Location = New Point(22, 7), .AutoSize = True, .Font = New Font("Segoe UI", 9), .BackColor = Color.Transparent}
        statusLbl = New Label With {.Text = "OFFLINE", .ForeColor = CLR_DANGER, .Font = New Font("Segoe UI", 8, FontStyle.Bold), .AutoSize = True, .Anchor = AnchorStyles.Right, .BackColor = Color.Transparent}
        statusLbl.Location = New Point(210, 8)
        p.Controls.AddRange({dot, lbl, statusLbl}) : p.Tag = dot
        Return p
    End Function

    Private Sub BuildIOBar(tlp As TableLayoutPanel)
        pnlIOBar = New Panel With {.Dock = DockStyle.Fill, .BackColor = Color.FromArgb(16, 16, 20), .Padding = New Padding(10, 4, 10, 4)}
        AddHandler pnlIOBar.Paint, Sub(s, e)
            Using p As New Pen(CLR_BORDER, 1) : e.Graphics.DrawLine(p, 0, 0, pnlIOBar.Width, 0) : End Using
        End Sub
        tlp.Controls.Add(pnlIOBar, 0, 2)

        Dim flp As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .FlowDirection = FlowDirection.LeftToRight, .WrapContents = False}

        ' DI label
        flp.Controls.Add(New Label With {.Text = "DI", .ForeColor = CLR_ACCENT, .Font = New Font("Consolas", 9, FontStyle.Bold), .AutoSize = True, .Margin = New Padding(0, 10, 4, 0)})
        For i = 0 To 15
            diLeds(i) = New Panel With {.Size = New Size(20, 20), .BackColor = Color.FromArgb(40, 40, 40), .Margin = New Padding(2, 8, 2, 0)}
            MakeCircle(diLeds(i))
            Dim tt As New ToolTip()
            tt.SetToolTip(diLeds(i), $"DI {i:D2}")
            flp.Controls.Add(diLeds(i))
        Next

        flp.Controls.Add(New Label With {.Text = "  DO", .ForeColor = CLR_WARN, .Font = New Font("Consolas", 9, FontStyle.Bold), .AutoSize = True, .Margin = New Padding(12, 10, 4, 0)})
        For i = 0 To 15
            doLeds(i) = New Panel With {.Size = New Size(20, 20), .BackColor = Color.FromArgb(40, 40, 40), .Margin = New Padding(2, 8, 2, 0)}
            MakeCircle(doLeds(i))
            Dim tt As New ToolTip()
            tt.SetToolTip(doLeds(i), $"DO {i:D2}")
            flp.Controls.Add(doLeds(i))
        Next

        pnlIOBar.Controls.Add(flp)
    End Sub

    Private Sub BuildFooter(tlp As TableLayoutPanel)
        pnlFooter = New Panel With {.Dock = DockStyle.Fill, .BackColor = Color.FromArgb(16, 16, 20), .Padding = New Padding(10, 8, 10, 8)}
        AddHandler pnlFooter.Paint, Sub(s, e)
            Using p As New Pen(CLR_BORDER, 1) : e.Graphics.DrawLine(p, 0, 0, pnlFooter.Width, 0) : End Using
        End Sub
        tlp.Controls.Add(pnlFooter, 0, 3) : tlp.SetColumnSpan(pnlFooter, 2)

        ' Log
        logDisplay = New ListBox With {
            .Dock = DockStyle.Left, .Width = 700, .BackColor = Color.FromArgb(10, 10, 14),
            .ForeColor = Color.FromArgb(0, 220, 120), .Font = New Font("Consolas", 9.5F),
            .BorderStyle = BorderStyle.None, .DrawMode = DrawMode.OwnerDrawFixed, .ItemHeight = 18
        }
        AddHandler logDisplay.DrawItem, AddressOf DrawLogItem
        pnlFooter.Controls.Add(logDisplay)

        ' Buttons
        Dim flpBtns As New FlowLayoutPanel With {
            .Dock = DockStyle.Right, .Width = 680,
            .FlowDirection = FlowDirection.LeftToRight, .Padding = New Padding(8, 10, 0, 0)
        }
        btnStart = CreateActionBtn("▶  START", "F1", CLR_PASS) : AddHandler btnStart.Click, Sub() isSoftwareStartRequested = True
        btnReset = CreateActionBtn("⟲  RESET", "F2", CLR_ACCENT) : AddHandler btnReset.Click, Sub() isSoftwareResetRequested = True
        btnPause = CreateActionBtn("❚❚  PAUSE", "F3", CLR_WARN) : AddHandler btnPause.Click, Sub()
            isPaused = Not isPaused
            btnPause.Text = If(isPaused, "▶  RESUME", "❚❚  PAUSE")
            btnPause.BackColor = If(isPaused, CLR_PASS, CLR_WARN)
        End Sub
        btnConfig = CreateActionBtn("⚙  CONFIG", "F5", Color.FromArgb(70, 70, 80)) : AddHandler btnConfig.Click, Sub() ShowConfigDialog()
        btnExit = CreateActionBtn("✕  EXIT", "ESC", CLR_DANGER) : AddHandler btnExit.Click, Sub() Application.Exit()
        flpBtns.Controls.AddRange({btnStart, btnReset, btnPause, btnConfig, btnExit})

        pnlFooter.Controls.Add(flpBtns)
    End Sub

    Private Function CreateActionBtn(text As String, hotkey As String, clr As Color) As Button
        Dim btn As New Button With {
            .Size = New Size(125, 60), .BackColor = clr, .ForeColor = Color.White,
            .FlatStyle = FlatStyle.Flat, .Font = New Font("Segoe UI Semibold", 11),
            .Margin = New Padding(4), .Cursor = Cursors.Hand, .TextAlign = ContentAlignment.MiddleCenter
        }
        btn.Text = text & vbCrLf & hotkey
        btn.FlatAppearance.BorderSize = 0
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(Math.Min(clr.R + 30, 255), Math.Min(clr.G + 30, 255), Math.Min(clr.B + 30, 255))
        Return btn
    End Function

    Private Sub DrawLogItem(sender As Object, e As DrawItemEventArgs)
        If e.Index < 0 Then Return
        e.DrawBackground()
        Dim txt = logDisplay.Items(e.Index).ToString()
        Dim clr = Color.FromArgb(0, 220, 120) ' default green
        If txt.Contains("ERROR") OrElse txt.Contains("E-STOP") OrElse txt.Contains("FAULT") Then clr = CLR_DANGER
        If txt.Contains("WARN") OrElse txt.Contains("SAFETY") Then clr = CLR_WARN
        If txt.Contains("SYSTEM") Then clr = CLR_ACCENT
        If txt.Contains("CYCLE") Then clr = Color.FromArgb(0, 200, 255)
        If txt.Contains("VISION") Then clr = Color.FromArgb(200, 160, 255)
        If txt.Contains("SCAN") OrElse txt.Contains("VERIFY") Then clr = Color.FromArgb(255, 220, 100)
        TextRenderer.DrawText(e.Graphics, txt, logDisplay.Font, e.Bounds, clr, TextFormatFlags.Left Or TextFormatFlags.VerticalCenter)
    End Sub

    Private Sub AddPaintBorder(ctrl As Control, clr As Color)
        AddHandler ctrl.Paint, Sub(s, e)
            ControlPaint.DrawBorder(e.Graphics, ctrl.ClientRectangle,
                clr, 1, ButtonBorderStyle.Solid,
                clr, 1, ButtonBorderStyle.Solid,
                clr, 1, ButtonBorderStyle.Solid,
                clr, 1, ButtonBorderStyle.Solid)
        End Sub
    End Sub

    Private Sub MakeCircle(p As Panel)
        Dim path As New GraphicsPath()
        path.AddEllipse(0, 0, p.Width, p.Height)
        p.Region = New Region(path)
    End Sub
#End Region

#Region "HMI Update"
    Private Sub UpdateHMI()
        If Me.InvokeRequired Then : Me.Invoke(Sub() UpdateHMI()) : Return : End If

        animPulse = (animPulse + 1) Mod 20

        ' Clock
        lblClock.Text = DateTime.Now.ToString("HH:mm:ss")

        ' State Badge
        lblStateText.Text = currentState.ToString().Replace("_", " ")
        Select Case currentState
            Case MachineStatus.IDLE, MachineStatus.CYCLE_COMPLETE
                lblStateText.ForeColor = CLR_PASS : lblStateText.BackColor = Color.FromArgb(20, 52, 199, 89)
            Case MachineStatus.FAULT_ALARM, MachineStatus.EMERGENCY_STOP, MachineStatus.MODEL_FAIL, MachineStatus.VISION_NG
                lblStateText.ForeColor = Color.White : lblStateText.BackColor = If(animPulse < 10, Color.FromArgb(180, 0, 0), Color.FromArgb(100, 0, 0))
            Case MachineStatus.DISPENSE_RUNNING, MachineStatus.DISPENSE_START
                lblStateText.ForeColor = CLR_WARN : lblStateText.BackColor = Color.FromArgb(40, 255, 149, 0)
            Case Else
                lblStateText.ForeColor = CLR_TEXT : lblStateText.BackColor = Color.FromArgb(36, 38, 44)
        End Select

        ' Stats
        lblStatsPass.Text = config.PassCount.ToString()
        lblStatsFail.Text = config.FailCount.ToString()
        lblStatsTime.Text = $"{cycleDuration:F1}s"
        Dim total = config.PassCount + config.FailCount
        lblStatsYield.Text = If(total > 0, $"{(config.PassCount * 100.0 / total):F1}%", "—")

        ' Model
        lblModelDisplay.Text = If(lastBarcode = "N/A", "NO MODEL", lastBarcode)

        ' Alarm
        If Not String.IsNullOrEmpty(alarmMessage) Then
            lblAlarmDetail.Text = "⚠ " & alarmMessage
            lblAlarmDetail.Height = 36
        Else
            lblAlarmDetail.Text = "" : lblAlarmDetail.Height = 0
        End If

        ' Connection indicators
        UpdateIndicator(indModbus, lblIndModbus, isModbusLive)
        UpdateIndicator(indVision, lblIndVision, isVisionLive)
        UpdateIndicator(indScanner, lblIndScanner, isScannerLive)

        ' I/O LEDs
        For i = 0 To 15
            diLeds(i).BackColor = If(inputs(i), Color.FromArgb(0, 220, 100), Color.FromArgb(40, 40, 40))
            doLeds(i).BackColor = If(outputs(i), Color.FromArgb(255, 180, 0), Color.FromArgb(40, 40, 40))
        Next

        ' Pause visual
        btnPause.Text = If(isPaused, "▶  RESUME" & vbCrLf & "F3", "❚❚  PAUSE" & vbCrLf & "F3")
        btnPause.BackColor = If(isPaused, CLR_PASS, CLR_WARN)

        ' Camera repaint for crosshair
        picCameraPreview.Invalidate()
    End Sub

    Private Sub UpdateIndicator(pnl As Panel, lbl As Label, isOnline As Boolean)
        Dim dot = CType(pnl.Tag, Panel)
        dot.BackColor = If(isOnline, CLR_PASS, CLR_DANGER)
        lbl.Text = If(isOnline, "ONLINE", "OFFLINE")
        lbl.ForeColor = If(isOnline, CLR_PASS, CLR_DANGER)
    End Sub

    Private Sub Log(category As String, msg As String)
        If logDisplay.InvokeRequired Then : logDisplay.Invoke(Sub() Log(category, msg)) : Return : End If
        Dim entry = $"[{DateTime.Now:HH:mm:ss}] {category.PadRight(8)} │ {msg}"
        logDisplay.Items.Insert(0, entry)
        If logDisplay.Items.Count > 200 Then logDisplay.Items.RemoveAt(200)
        DebugLog($"{category.PadRight(8)} | {msg}")
    End Sub
#End Region

#Region "Config Dialog"
    Private Sub ShowConfigDialog()
        Using f As New Form With {
            .Text = "⚙ VBX CONFIGURATION", .Size = New Size(460, 740),
            .BackColor = CLR_BG, .ForeColor = CLR_TEXT,
            .StartPosition = FormStartPosition.CenterParent,
            .FormBorderStyle = FormBorderStyle.FixedDialog,
            .MaximizeBox = False, .MinimizeBox = False,
            .AutoScroll = True
        }
            Dim flp As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(24, 20, 24, 10), .FlowDirection = FlowDirection.TopDown}

            ' Section: Network
            flp.Controls.Add(New Label With {.Text = "NETWORK CONFIGURATION", .ForeColor = CLR_ACCENT, .Font = New Font("Segoe UI", 10, FontStyle.Bold), .AutoSize = True, .Margin = New Padding(0, 0, 0, 10)})

            Dim tM = AddConfigField(flp, "Modbus PLC IP", config.ModbusIP)
            Dim tMP = AddConfigField(flp, "Modbus Port", config.ModbusPort.ToString())
            Dim tC = AddConfigField(flp, "Cognex Vision IP", config.CognexIP)
            Dim tCP = AddConfigField(flp, "Cognex Port", config.CognexPort.ToString())
            Dim tK = AddConfigField(flp, "Keyence Scanner IP", config.KeyenceIP)
            Dim tKP = AddConfigField(flp, "Keyence Port", config.KeyencePort.ToString())

            flp.Controls.Add(New Label With {.Text = "CAMERA SOURCE", .ForeColor = CLR_ACCENT, .Font = New Font("Segoe UI", 10, FontStyle.Bold), .AutoSize = True, .Margin = New Padding(0, 12, 0, 6)})
            Dim tCam = AddConfigField(flp, "Camera HTTP URL", config.CameraUrl)
            Dim tCamPath = AddConfigField(flp, "Camera FTP/TCP/File Path (empty = use HTTP)", config.CameraSourcePath)
            flp.Controls.Add(New Label With {.Text = "ftp://ip/path | tcp://ip:port | C:\path\img.jpg", .ForeColor = CLR_DIM, .Font = New Font("Segoe UI", 8), .AutoSize = True, .Margin = New Padding(0, 0, 0, 4)})

            flp.Controls.Add(New Label With {.Text = "PRODUCTION", .ForeColor = CLR_ACCENT, .Font = New Font("Segoe UI", 10, FontStyle.Bold), .AutoSize = True, .Margin = New Padding(0, 16, 0, 10)})
            Dim tB = AddConfigField(flp, "Master Barcode (* = any)", config.MasterBarcode)

            ' Reset counters button
            Dim btnResetCounters As New Button With {
                .Text = "RESET COUNTERS", .Height = 36, .Width = 380, .BackColor = CLR_WARN,
                .ForeColor = Color.White, .FlatStyle = FlatStyle.Flat, .Font = New Font("Segoe UI Semibold", 10),
                .Margin = New Padding(0, 12, 0, 0)
            }
            btnResetCounters.FlatAppearance.BorderSize = 0
            AddHandler btnResetCounters.Click, Sub()
                config.PassCount = 0 : config.FailCount = 0 : SaveSettings()
                MessageBox.Show("Counters reset to zero.", "Reset", MessageBoxButtons.OK, MessageBoxIcon.Information)
            End Sub
            flp.Controls.Add(btnResetCounters)

            ' Reset to factory defaults button
            Dim btnDefaults As New Button With {
                .Text = "RESET TO DEFAULTS", .Height = 36, .Width = 380, .BackColor = CLR_DANGER,
                .ForeColor = Color.White, .FlatStyle = FlatStyle.Flat, .Font = New Font("Segoe UI Semibold", 10),
                .Margin = New Padding(0, 6, 0, 0)
            }
            btnDefaults.FlatAppearance.BorderSize = 0
            AddHandler btnDefaults.Click, Sub()
                If MessageBox.Show("Reset all settings to factory defaults?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) = DialogResult.Yes Then
                    tM.Text = DEFAULT_MODBUS_IP
                    tMP.Text = DEFAULT_MODBUS_PORT.ToString()
                    tC.Text = DEFAULT_COGNEX_IP
                    tCP.Text = DEFAULT_COGNEX_PORT.ToString()
                    tK.Text = DEFAULT_KEYENCE_IP
                    tKP.Text = DEFAULT_KEYENCE_PORT.ToString()
                    tCam.Text = "http://192.168.1.20/img/snapshot.jpg"
                    tCamPath.Text = ""
                    tB.Text = "VBX-001"
                End If
            End Sub
            flp.Controls.Add(btnDefaults)

            f.Controls.Add(flp)

            Dim btnSave As New Button With {
                .Text = "SAVE & APPLY", .Height = 50, .Dock = DockStyle.Bottom,
                .BackColor = CLR_ACCENT, .ForeColor = Color.White, .FlatStyle = FlatStyle.Flat,
                .Font = New Font("Segoe UI Semibold", 12)
            }
            btnSave.FlatAppearance.BorderSize = 0
            AddHandler btnSave.Click, Sub()
                config.ModbusIP = tM.Text
                Integer.TryParse(tMP.Text, config.ModbusPort)
                config.CognexIP = tC.Text
                Integer.TryParse(tCP.Text, config.CognexPort)
                config.KeyenceIP = tK.Text
                Integer.TryParse(tKP.Text, config.KeyencePort)
                config.CameraUrl = tCam.Text : config.CameraSourcePath = tCamPath.Text
                config.MasterBarcode = tB.Text
                ' Force Modbus reconnect
                Try : modbusClient?.Disconnect() : Catch : End Try
                modbusClient = Nothing : modbusReconnectCooldown = 0
                SaveSettings()
                Log("CONFIG", "Settings saved and applied")
                f.Close()
            End Sub
            f.Controls.Add(btnSave)
            f.ShowDialog()
        End Using
    End Sub

    Private Function AddConfigField(p As FlowLayoutPanel, label As String, value As String) As TextBox
        p.Controls.Add(New Label With {.Text = label, .Width = 380, .ForeColor = CLR_DIM, .Font = New Font("Segoe UI", 9), .Margin = New Padding(0, 0, 0, 2)})
        Dim t As New TextBox With {
            .Text = value, .Width = 380, .Height = 28, .BackColor = CLR_CARD,
            .ForeColor = CLR_TEXT, .BorderStyle = BorderStyle.FixedSingle,
            .Font = New Font("Consolas", 11), .Margin = New Padding(0, 0, 0, 8)
        }
        p.Controls.Add(t)
        Return t
    End Function

    Private Sub SaveSettings()
        Try
            IO.File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(config))
        Catch ex As Exception
        End Try
    End Sub

    Private Sub LoadSettings()
        Try
            If IO.File.Exists(configPath) Then
                config = System.Text.Json.JsonSerializer.Deserialize(Of AppConfig)(IO.File.ReadAllText(configPath))
            End If
        Catch ex As Exception
        End Try
    End Sub
#End Region

#Region "Keyboard Shortcuts"
    Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
        Select Case keyData
            Case Keys.F1 : isSoftwareStartRequested = True : Return True
            Case Keys.F2 : isSoftwareResetRequested = True : Return True
            Case Keys.F3 : isPaused = Not isPaused : Return True
            Case Keys.F5 : ShowConfigDialog() : Return True
            Case Keys.Escape : Application.Exit() : Return True
        End Select
        Return MyBase.ProcessCmdKey(msg, keyData)
    End Function
#End Region

    <STAThread()>
    Public Shared Sub Main()
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)
        Application.Run(New Form1())
    End Sub
End Class

''' <summary>
''' Simple helper for vertical layouts
''' </summary>
Public Class StackPanelWithLayout
    Inherits FlowLayoutPanel
    Sub New()
        Me.FlowDirection = FlowDirection.TopDown
        Me.WrapContents = False
    End Sub
End Class
