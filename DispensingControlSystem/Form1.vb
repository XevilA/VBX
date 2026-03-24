Imports EasyModbus
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading.Tasks
Imports System.Drawing
Imports System.Windows.Forms
Imports System.Drawing.Drawing2D
Imports System.Linq

''' <summary>
''' VBX INDUSTRIAL DISPENSING CONTROL SYSTEM - PRO VERSION
''' Advanced HMI with Asynchronous Hardware Integration and Premium Industrial Design.
''' </summary>
Partial Class Form1
    Inherits Form

#Region "Hardware Configuration"
    Private Const DEFAULT_MODBUS_IP As String = "192.168.1.12"
    Private Const DEFAULT_MODBUS_PORT As Integer = 502
    Private Const DEFAULT_COGNEX_IP As String = "192.168.1.20"
    Private Const DEFAULT_COGNEX_PORT As Integer = 23
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
        Public Property MasterBarcode As String = "VBX-001"
        Public Property ProgramNames As String() = Enumerable.Range(1, 15).Select(Function(i) $"PROGRAM {i:D2}").ToArray()
    End Class

    Private config As New AppConfig()
    Private ReadOnly configPath As String = IO.Path.Combine(Application.StartupPath, "settings.config.json")
    Private cycleStartTime As DateTime
#End Region

#Region "Machine State System"
    Public Enum MachineStatus
        IDLE
        INIT_BUSY
        SCAN_READY
        SCAN_BUSY
        MODEL_VERIFY
        PROCESS_START
        PROCESS_RUNNING
        PROCESS_FINISH
        INSPECTION_BUSY
        CYCLE_COMPLETE
        FAULT_ALARM
        EMERGENCY_STOP
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
#End Region

#Region "Premium UI Components"
    ' Theme Colors (Industrial Onyx)
    Private ReadOnly COLOR_BG As Color = Color.FromArgb(18, 18, 18)
    Private ReadOnly COLOR_PANEL As Color = Color.FromArgb(28, 28, 30)
    Private ReadOnly COLOR_ACCENT As Color = Color.FromArgb(0, 122, 255) ' Azure Blue
    Private ReadOnly COLOR_TEXT As Color = Color.FromArgb(242, 242, 247)
    Private ReadOnly COLOR_TEXT_DIM As Color = Color.FromArgb(142, 142, 147)
    
    Private lblTitle, lblClock, lblStateText, lblModelDisplay, lblAlarmDetail As Label
    Private pnlStatusHeader, pnlSideBar, pnlMainAction, pnlFooter As Panel
    Private picCameraPreview As PictureBox
    Private btnActionStart, btnActionReset, btnActionPause, btnActionConfig, btnActionExit As Button
    Private indModbus, indVision, indScanner As Panel
    Private logDisplay As ListBox
    Private mainTimer, cameraTimer As Timer
    Private lblStatsPass, lblStatsFail, lblStatsTime As Label
    Private cbProgramSelect As ComboBox
    Private isModbusLive, isVisionLive, isScannerLive, isProcessing, isNetWorking As Boolean
#End Region

    ''' <summary>
    ''' Core Machine Logic - Refined State Machine
    ''' </summary>
    Private Async Function RunWorkflowAsync() As Task
        ' Capture Triggers
        Dim triggerStart = isSoftwareStartRequested OrElse (inputs(1) AndAlso Not inputs(0))
        Dim triggerReset = isSoftwareResetRequested
        isSoftwareStartRequested = False
        isSoftwareResetRequested = False

        ' Handle Emergency / Reset
        If triggerReset OrElse inputs(0) = False Then ' DI 00 is Safety (NC)
            If Not inputs(0) Then currentState = MachineStatus.EMERGENCY_STOP
            If triggerReset Then
                currentState = MachineStatus.IDLE
                isEStopActive = False
                ResetOutputs()
                Log("SYSTEM", "User Manual Reset Initiated")
            End If
            If currentState = MachineStatus.EMERGENCY_STOP Then
                outputs(0) = True : outputs(3) = True ' Red Light + Robot E-Stop
                Log("ERROR", "EMERGENCY STOP ACTIVE (I/O DI-00)")
                Return
            End If
        End If

        Select Case currentState
            Case MachineStatus.IDLE
                ResetOutputs()
                outputs(2) = True ' Green Light (Ready)
                If triggerStart Then 
                    currentState = MachineStatus.INIT_BUSY
                    cycleStartTime = DateTime.Now
                    Log("CYCLE", "Production Started")
                End If

            Case MachineStatus.INIT_BUSY
                outputs(2) = False : outputs(10) = True ' Clamp Active
                Await Task.Delay(800) ' Cylinder Move Time
                If inputs(3) Then ' Cylinder Sensor Check
                    currentState = MachineStatus.SCAN_BUSY
                Else
                    Log("WARN", "Cylinder Sensor Timeout")
                    currentState = MachineStatus.FAULT_ALARM
                End If

            Case MachineStatus.SCAN_BUSY
                Log("SCAN", "Acquiring Barcode...")
                Dim barcode = Await ReadBarcodeAsync()
                If Not String.IsNullOrEmpty(barcode) AndAlso barcode <> "ERROR" AndAlso barcode <> "ER" Then
                    lastBarcode = barcode
                    isScannerLive = True
                    currentState = MachineStatus.MODEL_VERIFY
                Else
                    isScannerLive = False
                    Log("SCAN", "Barcode Read Failed - Retrying...")
                    Await Task.Delay(500)
                End If

            Case MachineStatus.MODEL_VERIFY
                If lastBarcode = config.MasterBarcode OrElse config.MasterBarcode = "*" Then
                    Log("VERIFY", $"Model Match: {lastBarcode}")
                    currentState = MachineStatus.PROCESS_START
                Else
                    Log("VERIFY", $"Mismatch! Expected: {config.MasterBarcode}, Found: {lastBarcode}")
                    currentState = MachineStatus.FAULT_ALARM
                End If

            Case MachineStatus.PROCESS_START
                outputs(1) = True ' Yellow (Busy)
                ' Send Program Number Binary
                UpdateProgramBits()
                outputs(4) = True ' Robot Start Signal
                Await Task.Delay(500) ' Pulse
                outputs(4) = False
                currentState = MachineStatus.PROCESS_RUNNING

            Case MachineStatus.PROCESS_RUNNING
                If inputs(4) Then ' Robot Done
                    currentState = MachineStatus.INSPECTION_BUSY
                ElseIf inputs(5) Then ' Robot Fault
                    Log("ERROR", "Robot Reported Internal Fault")
                    currentState = MachineStatus.FAULT_ALARM
                End If

            Case MachineStatus.INSPECTION_BUSY
                Log("VISION", "Triggering Cognex Inspection...")
                Dim result = Await SendTcpHandshakeAsync(config.CognexIP, config.CognexPort, "T" & vbCr)
                lastVisionResult = result
                If result.ToUpper().Contains("OK") OrElse result.Contains("1") OrElse inputs(6) Then
                    Log("VISION", "Inspection PASSED")
                    config.PassCount += 1
                    currentState = MachineStatus.CYCLE_COMPLETE
                Else
                    Log("VISION", "Inspection FAILED")
                    config.FailCount += 1
                    currentState = MachineStatus.FAULT_ALARM
                End If
                SaveSettings()

            Case MachineStatus.CYCLE_COMPLETE
                outputs(10) = False ' Unclamp
                If Not inputs(3) Then ' Cylinder Retracted
                    cycleDuration = (DateTime.Now - cycleStartTime).TotalSeconds
                    Log("CYCLE", $"Successfully Finished ({cycleDuration:F2}s)")
                    currentState = MachineStatus.IDLE
                End If

            Case MachineStatus.FAULT_ALARM
                outputs(0) = True : outputs(1) = False : outputs(2) = False
                If triggerReset Then currentState = MachineStatus.IDLE
        End Select
    End Function

#Region "Refined Hardware Handlers"
    Private Async Function ReadBarcodeAsync() As Task(Of String)
        Try
            Using client As New TcpClient()
                Dim connectTask = client.ConnectAsync(config.KeyenceIP, config.KeyencePort)
                If Await Task.WhenAny(connectTask, Task.Delay(2000)) Is connectTask Then
                    Using stream = client.GetStream()
                        Dim cmd = Encoding.ASCII.GetBytes("LON" & vbCr)
                        Await stream.WriteAsync(cmd, 0, cmd.Length)
                        
                        Dim buffer(1024) As Byte
                        Dim readTask = stream.ReadAsync(buffer, 0, buffer.Length)
                        If Await Task.WhenAny(readTask, Task.Delay(2500)) Is readTask Then
                            Dim bytesRead = Await readTask
                            Dim response = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim()
                            
                            cmd = Encoding.ASCII.GetBytes("LOFF" & vbCr)
                            Await stream.WriteAsync(cmd, 0, cmd.Length)
                            Return response
                        End If
                    End Using
                End If
            End Using
        Catch ex As Exception
            Return "ERROR"
        End Try
        Return "TIMEOUT"
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
        Catch : Return "NET_ERR" : End Try
        Return "TIMEOUT"
    End Function

    Private Sub ResetOutputs()
        For i = 0 To 15 : outputs(i) = False : Next
    End Sub

    Private Sub UpdateProgramBits()
        Dim progNum = cbProgramSelect.SelectedIndex + 1
        outputs(6) = (progNum And 1) <> 0
        outputs(7) = (progNum And 2) <> 0
        outputs(8) = (progNum And 4) <> 0
        outputs(9) = (progNum And 8) <> 0
    End Sub

    Private Async Sub SyncModbusAsync(sender As Object, e As EventArgs)
        If isProcessing Then Return
        isProcessing = True
        Try
            ' Reconnect if needed
            If modbusClient Is Nothing OrElse Not modbusClient.Connected Then
                If Not isNetWorking Then
                    isNetWorking = True
                    Task.Run(Sub()
                        Try
                            modbusClient = New ModbusClient(config.ModbusIP, config.ModbusPort)
                            modbusClient.Connect()
                        Catch : Finally : isNetWorking = False : End Try
                    End Sub)
                End If
            End If

            If modbusClient IsNot Nothing AndAlso modbusClient.Connected Then
                isModbusLive = True
                ' Read Inputs
                Dim di = Await Task.Run(Function() modbusClient.ReadDiscreteInputs(0, 16))
                For i = 0 To 15 : inputs(i) = di(i) : Next

                ' Execute Logic
                If Not isPaused Then Await RunWorkflowAsync()

                ' Write Outputs
                Await Task.Run(Sub() modbusClient.WriteMultipleCoils(0, outputs))
            Else
                isModbusLive = False
                modbusClient = Nothing
            End If
            
            UpdateHMI_Elements()
        Catch : Finally : isProcessing = False : End Try
    End Sub

    Private Async Sub UpdateCameraFrameAsync(sender As Object, e As EventArgs)
        If picCameraPreview Is Nothing OrElse isPaused Then Return
        Try
            Using http As New System.Net.Http.HttpClient()
                http.Timeout = TimeSpan.FromMilliseconds(2500)
                Dim data = Await http.GetByteArrayAsync(config.CameraUrl)
                Using ms As New IO.MemoryStream(data)
                    Dim bmp = New Bitmap(ms)
                    picCameraPreview.Invoke(Sub()
                        If picCameraPreview.Image IsNot Nothing Then picCameraPreview.Image.Dispose()
                        picCameraPreview.Image = bmp
                    End Sub)
                End Using
            End Using
            isVisionLive = True
        Catch
            isVisionLive = False
        End Try
    End Sub
#End Region

#Region "HMI Initialization & Redesign"
    Public Sub New()
        InitializeComponent()
        Me.Text = "VBX DISPENSING CONTROL v2.0"
        Me.BackColor = COLOR_BG
        Me.DoubleBuffered = True
        Me.FormBorderStyle = FormBorderStyle.None
        Me.WindowState = FormWindowState.Maximized

        LoadSettings()
        SetupLayout()
        
        mainTimer = New Timer With {.Interval = 100}
        AddHandler mainTimer.Tick, AddressOf SyncModbusAsync
        mainTimer.Start()

        cameraTimer = New Timer With {.Interval = 1000}
        AddHandler cameraTimer.Tick, AddressOf UpdateCameraFrameAsync
        cameraTimer.Start()
        
        Log("SYSTEM", "HMI Environment Initialized")
    End Sub

    Private Sub SetupLayout()
        Dim tlpRoot As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 2, .RowCount = 3}
        tlpRoot.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 75))
        tlpRoot.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 25))
        tlpRoot.RowStyles.Add(New RowStyle(SizeType.Absolute, 90))
        tlpRoot.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        tlpRoot.RowStyles.Add(New RowStyle(SizeType.Absolute, 200))
        Me.Controls.Add(tlpRoot)

        ' -- HEADER --
        pnlStatusHeader = New Panel With {.Dock = DockStyle.Fill, .BackColor = COLOR_PANEL, .Margin = New Padding(0)}
        tlpRoot.Controls.Add(pnlStatusHeader, 0, 0) : tlpRoot.SetColumnSpan(pnlStatusHeader, 2)
        
        lblTitle = New Label With {.Text = "VBX DISPENSING MACHINE", .ForeColor = COLOR_ACCENT, .Font = New Font("Segoe UI Semibold", 22), .Location = New Point(20, 10), .AutoSize = True}
        lblClock = New Label With {.Text = "00:00:00", .ForeColor = COLOR_TEXT_DIM, .Font = New Font("Consolas", 18), .Location = New Point(1020, 30), .Anchor = AnchorStyles.Right}
        lblStateText = New Label With {.Text = "SYSTEM IDLE", .ForeColor = Color.Lime, .Font = New Font("Segoe UI", 24, FontStyle.Bold), .TextAlign = ContentAlignment.MiddleCenter, .Size = New Size(500, 70), .Location = New Point(420, 10), .BackColor = Color.FromArgb(40, 40, 40)}
        pnlStatusHeader.Controls.AddRange({lblTitle, lblClock, lblStateText})

        ' -- MAIN WORK AREA --
        Dim pnlWorkspace As New Panel With {.Dock = DockStyle.Fill, .Padding = New Padding(15)}
        tlpRoot.Controls.Add(pnlWorkspace, 0, 1)
        
        picCameraPreview = New PictureBox With {.Dock = DockStyle.Fill, .BackColor = Color.Black, .SizeMode = PictureBoxSizeMode.Zoom, .BorderStyle = BorderStyle.FixedSingle}
        pnlWorkspace.Controls.Add(picCameraPreview)

        ' -- SIDEBAR (STATS & STATUS) --
        pnlSideBar = New Panel With {.Dock = DockStyle.Fill, .BackColor = COLOR_PANEL, .Padding = New Padding(15)}
        tlpRoot.Controls.Add(pnlSideBar, 1, 1)
        
        lblModelDisplay = New Label With {.Text = "NO MODEL", .ForeColor = Color.White, .Font = New Font("Segoe UI", 20, FontStyle.Bold), .Dock = DockStyle.Top, .Height = 50, .TextAlign = ContentAlignment.MiddleCenter, .BackColor = Color.FromArgb(50, 50, 52)}
        Dim pStats As New FlowLayoutPanel With {.Dock = DockStyle.Top, .Height = 180, .FlowDirection = FlowDirection.TopDown, .Padding = New Padding(0, 15, 0, 0)}
        lblStatsPass = New Label With {.Text = "PASS: 0", .ForeColor = Color.Lime, .Font = New Font("Segoe UI", 24), .AutoSize = True}
        lblStatsFail = New Label With {.Text = "FAIL: 0", .ForeColor = Color.OrangeRed, .Font = New Font("Segoe UI", 24), .AutoSize = True}
        lblStatsTime = New Label With {.Text = "T-TIME: 0.0s", .ForeColor = Color.Yellow, .Font = New Font("Segoe UI", 24), .AutoSize = True}
        pStats.Controls.AddRange({lblStatsPass, lblStatsFail, lblStatsTime})
        
        Dim pIndicators As New StackPanelWithLayout With {.Dock = DockStyle.Fill, .Padding = New Padding(0, 20, 0, 0)}
        indModbus = CreateIndicator("MODBUS TCP I/O")
        indVision = CreateIndicator("COGNEX VISION")
        indScanner = CreateIndicator("KEYENCE SCANNER")
        pIndicators.Controls.AddRange({indModbus, indVision, indScanner})
        
        cbProgramSelect = New ComboBox With {.Width = 260, .Font = New Font("Segoe UI", 12), .BackColor = Color.Black, .ForeColor = Color.White, .DropDownStyle = ComboBoxStyle.DropDownList, .Margin = New Padding(0, 15, 0, 0)}
        cbProgramSelect.Items.AddRange(config.ProgramNames) : cbProgramSelect.SelectedIndex = 0
        
        pnlSideBar.Controls.AddRange({cbProgramSelect, pIndicators, pStats, lblModelDisplay})

        ' -- FOOTER (LOGS & CONTROLS) --
        pnlFooter = New Panel With {.Dock = DockStyle.Fill, .BackColor = Color.FromArgb(20, 20, 22), .Padding = New Padding(15)}
        tlpRoot.Controls.Add(pnlFooter, 0, 2) : tlpRoot.SetColumnSpan(pnlFooter, 2)
        
        logDisplay = New ListBox With {.Dock = DockStyle.Left, .Width = 600, .BackColor = Color.Black, .ForeColor = Color.Lime, .Font = New Font("Consolas", 9), .BorderStyle = BorderStyle.None}
        pnlFooter.Controls.Add(logDisplay)
        
        Dim flpControls = New FlowLayoutPanel With {.Dock = DockStyle.Right, .Width = 720, .FlowDirection = FlowDirection.LeftToRight, .Padding = New Padding(10)}
        btnActionStart = CreateGlassBtn("START [F1]", Color.FromArgb(40, 180, 40)) : AddHandler btnActionStart.Click, Sub() isSoftwareStartRequested = True
        btnActionReset = CreateGlassBtn("RESET [F2]", Color.FromArgb(40, 40, 180)) : AddHandler btnActionReset.Click, Sub() isSoftwareResetRequested = True
        btnActionPause = CreateGlassBtn("PAUSE [F3]", Color.FromArgb(180, 120, 0)) : AddHandler btnActionPause.Click, Sub() isPaused = Not isPaused
        btnActionConfig = CreateGlassBtn("CONFIG [F5]", Color.FromArgb(80, 80, 80)) : AddHandler btnActionConfig.Click, Sub() ShowConfigDialog()
        btnActionExit = CreateGlassBtn("EXIT [ESC]", Color.FromArgb(120, 0, 0)) : AddHandler btnActionExit.Click, Sub() Application.Exit()
        flpControls.Controls.AddRange({btnActionStart, btnActionReset, btnActionPause, btnActionConfig, btnActionExit})
        pnlFooter.Controls.Add(flpControls)
    End Sub

    Private Function CreateIndicator(txt As String) As Panel
        Dim p As New Panel With {.Size = New Size(280, 40)}
        Dim dot As New Panel With {.Size = New Size(12, 12), .Location = New Point(5, 12), .BackColor = Color.Maroon}
        Dim lbl As New Label With {.Text = txt, .ForeColor = COLOR_TEXT, .Location = New Point(25, 10), .AutoSize = True, .Font = New Font("Segoe UI", 10)}
        p.Controls.AddRange({dot, lbl}) : p.Tag = dot : Return p
    End Function

    Private Function CreateGlassBtn(txt As String, baseColor As Color) As Button
        Dim btn As New Button With {
            .Text = txt, .Size = New Size(130, 70), .BackColor = baseColor, .ForeColor = Color.White,
            .FlatStyle = FlatStyle.Flat, .Font = New Font("Segoe UI Semibold", 11), .Margin = New Padding(5)
        }
        btn.FlatAppearance.BorderSize = 0
        Return btn
    End Function

    Private Sub Log(category As String, msg As String)
        If logDisplay.InvokeRequired Then : logDisplay.Invoke(Sub() Log(category, msg)) : Return : End If
        logDisplay.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {category.PadRight(10)} | {msg}")
        If logDisplay.Items.Count > 100 Then logDisplay.Items.RemoveAt(100)
    End Sub

    Private Sub UpdateHMI_Elements()
        If Me.InvokeRequired Then : Me.Invoke(Sub() UpdateHMI_Elements()) : Return : End If
        
        lblClock.Text = DateTime.Now.ToString("HH:mm:ss")
        lblStateText.Text = currentState.ToString().Replace("_", " ")
        lblStateText.BackColor = If(currentState = MachineStatus.FAULT_ALARM Or currentState = MachineStatus.EMERGENCY_STOP, Color.Maroon, Color.FromArgb(40, 40, 40))
        lblStateText.ForeColor = If(currentState = MachineStatus.IDLE, Color.Lime, Color.White)
        
        lblStatsPass.Text = $"PASS: {config.PassCount}"
        lblStatsFail.Text = $"FAIL: {config.FailCount}"
        lblStatsTime.Text = $"T-TIME: {cycleDuration:F1}s"
        lblModelDisplay.Text = lastBarcode

        CType(indModbus.Tag, Panel).BackColor = If(isModbusLive, Color.Lime, Color.Red)
        CType(indVision.Tag, Panel).BackColor = If(isVisionLive, Color.Lime, Color.Red)
        CType(indScanner.Tag, Panel).BackColor = If(isScannerLive, Color.Lime, Color.Red)
    End Sub

    Private Sub ShowConfigDialog()
        Using f As New Form With {.Text = "VBX CONFIGURATION", .Size = New Size(400, 500), .BackColor = COLOR_BG, .ForeColor = COLOR_TEXT, .StartPosition = FormStartPosition.CenterParent, .FormBorderStyle = FormBorderStyle.FixedDialog}
            Dim flp As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(20)}
            Dim tM = AddField(flp, "Modbus IP", config.ModbusIP)
            Dim tC = AddField(flp, "Cognex IP", config.CognexIP)
            Dim tK = AddField(flp, "Scanner IP", config.KeyenceIP)
            Dim tB = AddField(flp, "Master Barcode", config.MasterBarcode)
            Dim btnSave As New Button With {.Text = "SAVE SETTINGS", .Height = 50, .Dock = DockStyle.Bottom, .BackColor = COLOR_ACCENT, .FlatStyle = FlatStyle.Flat}
            AddHandler btnSave.Click, Sub()
                config.ModbusIP = tM.Text : config.CognexIP = tC.Text : config.KeyenceIP = tK.Text : config.MasterBarcode = tB.Text
                SaveSettings() : f.Close()
            End Sub
            f.Controls.Add(btnSave) : f.ShowDialog()
        End Using
    End Sub

    Private Function AddField(p As FlowLayoutPanel, label As String, value As String) As TextBox
        p.Controls.Add(New Label With {.Text = label, .Width = 350, .ForeColor = COLOR_TEXT_DIM})
        Dim t As New TextBox With {.Text = value, .Width = 340, .BackColor = Color.Black, .ForeColor = Color.White, .BorderStyle = BorderStyle.FixedSingle}
        p.Controls.Add(t) : Return t
    End Function

    Private Sub SaveSettings()
        Try : IO.File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(config)) : Catch : End Try
    End Sub

    Private Sub LoadSettings()
        Try : If IO.File.Exists(configPath) Then config = System.Text.Json.JsonSerializer.Deserialize(Of AppConfig)(IO.File.ReadAllText(configPath)) : Catch : End Try
    End Sub
#End Region

    Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
        If keyData = Keys.F1 Then : isSoftwareStartRequested = True : Return True : End If
        If keyData = Keys.F2 Then : isSoftwareResetRequested = True : Return True : End If
        If keyData = Keys.F3 Then : isPaused = Not isPaused : Return True : End If
        If keyData = Keys.Escape Then : Application.Exit() : Return True : End If
        Return MyBase.ProcessCmdKey(msg, keyData)
    End Function

    <STAThread()>
    Public Shared Sub Main()
        Application.EnableVisualStyles()
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
