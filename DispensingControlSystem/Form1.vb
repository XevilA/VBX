Imports EasyModbus
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading.Tasks
Imports System.Drawing

''' <summary>
''' Elite Industrial Automation Software - Dispensing Control System
''' Master Controller for Modbus TCP, Janome Robot, Cognex Vision, and Keyence Barcode.
''' </summary>
Public Class Form1
    Inherits Form

    <STAThread()>
    Public Shared Sub Main()
        ' Add Global Unhandled Exception Handlers
        AddHandler AppDomain.CurrentDomain.UnhandledException, AddressOf GlobalExceptionHandler
        AddHandler Application.ThreadException, AddressOf ThreadExceptionHandler
        
        Try
            Application.EnableVisualStyles()
            Application.SetCompatibleTextRenderingDefault(False)
            
            Dim mainForm As New Form1()
            Application.Run(mainForm)
        Catch ex As Exception
            LogFatalError(ex)
        End Try
    End Sub

    Private Shared Sub GlobalExceptionHandler(sender As Object, e As UnhandledExceptionEventArgs)
        LogFatalError(CType(e.ExceptionObject, Exception))
    End Sub

    Private Shared Sub ThreadExceptionHandler(sender As Object, e As System.Threading.ThreadExceptionEventArgs)
        LogFatalError(e.Exception)
    End Sub

    Private Shared Sub LogFatalError(ex As Exception)
        Dim crashPath = IO.Path.Combine(Application.StartupPath, "CRASH_LOG.txt")
        Dim msg = vbCrLf & "==== FATAL ERROR [" & DateTime.Now.ToString() & "] ====" & vbCrLf & ex.ToString()
        
        If TypeOf ex Is IO.FileNotFoundException Then
            Dim fnex = CType(ex, IO.FileNotFoundException)
            msg &= vbCrLf & "FILE NOT FOUND: " & fnex.FileName
            msg &= vbCrLf & "FUSION LOG: " & fnex.FusionLog
        End If
        
        Try
            IO.File.AppendAllText(crashPath, msg)
        Catch : End Try
        
        MessageBox.Show("CRITICAL ERROR: " & ex.Message & vbCrLf & "Check CRASH_LOG.txt for details.", "FATAL", MessageBoxButtons.OK, MessageBoxIcon.Error)
        Environment.Exit(-1)
    End Sub

#Region "Hardware Configuration Constants"
    ' AMSAMOTION MT3A-IO1632
    Private Const MODBUS_IP As String = "192.168.1.50"
    Private Const MODBUS_PORT As Integer = 502

    ' Cognex In-Sight 2800
    Private Const COGNEX_IP As String = "192.168.1.20"
    Private Const COGNEX_PORT As Integer = 23

    ' Keyence Barcode Scanner
    Private Const KEYENCE_IP As String = "192.168.1.10"
    Private Const KEYENCE_PORT As Integer = 23
#End Region

#Region "Industrial UI Controls (Programmatic)"
    Private lblTitle As Label
    Private lblClock As Label
    Private lblMachineState As Label
    Private pnlTowerRed As Panel
    Private pnlTowerYellow As Panel
    Private pnlTowerGreen As Panel
    Private txtScannedModel As TextBox
    Private txtVisionResult As TextBox
    Private btnStart As Button
    Private btnReset As Button
    Private btnPause As Button 
    Private btnEStop As Button
    Private btnSettings As Button ' New
    Private picCamera As PictureBox ' New: Vision Live
    Private picStatusModbus As Panel
    Private picStatusVision As Panel
    Private picStatusScanner As Panel
    Private lstLog As ListBox
    Private dgvHistory As DataGridView ' New: Barcode History
    Private tmrMainLoop As Timer
    Private tmrCamera As Timer ' New: Camera Refresh
    
    ' Stats Controls
    Private lblPassCount As Label
    Private lblFailCount As Label
    Private lblCycleTime As Label
    Private cmbProgramSelect As ComboBox ' New: DO 07-10
#End Region

#Region "System Configuration & Stats"
    Public Class AppConfig
        Public Property ModbusIP As String = "192.168.1.50"
        Public Property ModbusPort As Integer = 502
        Public Property CognexIP As String = "192.168.1.20"
        Public Property CognexPort As Integer = 23
        Public Property KeyenceIP As String = "192.168.1.10"
        Public Property KeyencePort As Integer = 23
        Public Property PassCount As Integer = 0
        Public Property FailCount As Integer = 0
        Public Property CameraUrl As String = "http://192.168.1.20/img/snapshot.jpg"
        Public Property MasterBarcode As String = "12345"
        Public Property ProgramNames As String() = {"Program 1", "Program 2", "Program 3", "Program 4", "Program 5", "Program 6", "Program 7", "Program 8", "Program 9", "Program 10", "Program 11", "Program 12", "Program 13", "Program 14", "Program 15"}
    End Class

    Private config As New AppConfig()
    Private ReadOnly configPath As String = IO.Path.Combine(Application.StartupPath, "settings.json")
    Private cycleStartTime As DateTime
#End Region

#Region "Machine State Variables"
    Public Enum MachineStatus
        IDLE
        A1_WAIT_RETRACT     ' Wait for Cylinder Retract Sensor (DI 03)
        A1_READY            ' Ready for new part
        A1_BARCODE_SCAN     ' Scan barcode
        WRONG_MODEL         ' Alarm wrong model
        B1_READY            ' Wait for start & curtain sensor
        B1_DISPENSE_START   ' Robot Start Output (DO 04)
        B1_DISPENSE_WAIT    ' Wait for Robot Complete (DI 04)
        B1_DISPENSE_POST    ' Yellow Off
        VISION_CHECK        ' Trigger Vision (TCP or DI 06/07)
        FINISH_WAIT_ROBOT   ' Wait for Robot Home
        FINISH_SUCCESS      ' Cylinder Retract & Unclamp (DO 10)
        ALARM
    End Enum

    Public Enum BlinkColor
        NONE
        YELLOW
        GREEN
    End Enum

    Private currentState As MachineStatus = MachineStatus.IDLE
    Private currentBlinkColor As BlinkColor = BlinkColor.NONE
    Private robotWaitStart As DateTime
    Private blinkState As Boolean = False
    Private blinkCounter As Integer = 0
    Private modbusClient As ModbusClient
    Private inputs(15) As Boolean
    Private outputs(15) As Boolean
    
    ' I/O Mapping (Strictly following provided PC I/O table)
    ' Inputs: 0=E-Stop, 1=Start, 2=Curtain, 3=Retract Sens, 4=Robot Compl, 5=Robot Fail, 6=Vision OK, 7=Vision NG
    ' Outputs: 0=Red, 1=Yellow, 2=Green, 3=Robot E-Stop, 4=Robot Start, 5=Robot Pause, 6-9=Prog bits, 10=Clamp
    
    Private isEStopActive As Boolean = False
    Private isPaused As Boolean = False 
    Private ReadOnly logPath As String = IO.Path.Combine(Application.StartupPath, "ProductionLog.csv")
    
    ' Integrated Config Controls
    Private pnlMainDashboard As Panel
    Private pnlSettingsView As Panel
    Private txtCfgModbusIP, txtCfgModbusPort, txtCfgVisionIP, txtCfgVisionPort, txtCfgScannerIP, txtCfgScannerPort, txtCfgCameraUrl, txtCfgMasterBarcode, txtCfgProgramNames As TextBox
    Private btnCfgSave, btnCfgCancel As Button
#End Region

#Region "System Startup & UI Constructor"
    Public Sub New()
        InitializeComponent()
        ' Early Diagnostic Logging
        Try
            LoadSettings()
            SetupIndustrialHMI()
            Log("--- SYSTEM BOOTSTRAPPED ---")
            
            If Not IO.File.Exists(logPath) Then
                IO.File.WriteAllText(logPath, "Timestamp,Barcode,VisionResult,CycleTimeSec" & vbCrLf)
            End If
            Log("System Bootstrapped. Ready for Production.")
        Catch ex As Exception
            ' If UI fails, try to write to a local file
            IO.File.AppendAllText(IO.Path.Combine(Application.StartupPath, "CRASH_LOG.txt"), _
                DateTime.Now.ToString() & ": Constructor Crash: " & ex.ToString() & vbCrLf)
            MessageBox.Show("LAUNCH ERROR: " & ex.Message, "FATAL", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub LoadSettings()
        Try
            If IO.File.Exists(configPath) Then
                Dim json = IO.File.ReadAllText(configPath)
                config = System.Text.Json.JsonSerializer.Deserialize(Of AppConfig)(json)
            End If
        Catch ex As Exception
            Log("CONFIG LOAD FAIL: " & ex.Message)
        End Try
    End Sub

    Private Sub SaveSettings()
        Try
            Dim json = System.Text.Json.JsonSerializer.Serialize(config)
            IO.File.WriteAllText(configPath, json)
        Catch ex As Exception
            Log("CONFIG SAVE FAIL: " & ex.Message)
        End Try
    End Sub

    Private Sub SetupIndustrialHMI()
        ' KIOSK MODE: Full Screen, No Borders, TopMost
        Me.BackColor = Color.FromArgb(30, 30, 30) 
        Me.FormBorderStyle = FormBorderStyle.None
        Me.WindowState = FormWindowState.Maximized
        Me.TopMost = True
        Me.Text = "VBX HMI - PRODUCTION SYSTEM"
        Me.Padding = New Padding(10)

        ' Root Layout
        Dim rootTlp = New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 3}
        rootTlp.RowStyles.Add(New RowStyle(SizeType.Absolute, 70))  ' Header
        rootTlp.RowStyles.Add(New RowStyle(SizeType.Percent, 100))  ' Content Area (Swappable)
        rootTlp.RowStyles.Add(New RowStyle(SizeType.Absolute, 180)) ' Footer
        
        ' Create View Containers
        pnlMainDashboard = New Panel With {.Dock = DockStyle.Fill, .Margin = New Padding(0)}
        pnlSettingsView = New Panel With {.Dock = DockStyle.Fill, .Margin = New Padding(0), .Visible = False}
        
        rootTlp.Controls.Add(pnlMainDashboard, 0, 1)
        rootTlp.Controls.Add(pnlSettingsView, 0, 1) ' Same cell but only one visible at a time

        ' 1. Header Row
        Dim headerPnl = New Panel With {.Dock = DockStyle.Fill, .Margin = New Padding(0)}
        lblTitle = New Label With {.Text = "VBX DISPENSING MACHINE CONTROLLER", .ForeColor = Color.White, .Font = New Font("Segoe UI", 28, FontStyle.Bold), .Location = New Point(10, 10), .AutoSize = True}
        lblClock = New Label With {.ForeColor = Color.Cyan, .Font = New Font("Consolas", 24, FontStyle.Bold), .AutoSize = False, .TextAlign = ContentAlignment.MiddleRight, .Dock = DockStyle.Right, .Width = 260}
        
        btnEStop = CreateIndustrialButton("QUIT / ESC", Color.FromArgb(80, 20, 20), 150, 50)
        btnEStop.Dock = DockStyle.Right
        btnEStop.Margin = New Padding(5)
        
        headerPnl.Controls.AddRange({lblTitle, btnEStop, lblClock})
        rootTlp.Controls.Add(headerPnl, 0, 0)

        ' 2. Main Content Row (3 Columns) - NOW PART OF pnlMainDashboard
        Dim mainTlp = New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 3, .RowCount = 1, .Margin = New Padding(0)}
        mainTlp.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 35)) ' Left
        mainTlp.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 40)) ' Center (Camera)
        mainTlp.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 25)) ' Right (Stats)
        pnlMainDashboard.Controls.Add(mainTlp)
        
        ' 2S. Settings View Construction
        SetupSettingsPanel()

        ' 2A. Left Panel (Machine State + Tower + History)
        Dim leftTlp = New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 3}
        leftTlp.RowStyles.Add(New RowStyle(SizeType.Absolute, 150)) ' State
        leftTlp.RowStyles.Add(New RowStyle(SizeType.Absolute, 80))  ' Tower
        leftTlp.RowStyles.Add(New RowStyle(SizeType.Percent, 100))  ' History
        
        lblMachineState = New Label With {.Text = "SYSTEM IDLE", .ForeColor = Color.White, .BackColor = Color.FromArgb(45, 45, 45), .Font = New Font("Segoe UI", 48, FontStyle.Bold), .Dock = DockStyle.Fill, .TextAlign = ContentAlignment.MiddleCenter, .BorderStyle = BorderStyle.FixedSingle, .Margin = New Padding(0, 0, 0, 10)}
        leftTlp.Controls.Add(lblMachineState, 0, 0)

        Dim towerFlow = New FlowLayoutPanel With {.Dock = DockStyle.Fill, .FlowDirection = FlowDirection.LeftToRight, .AutoSize = True}
        pnlTowerRed = CreateTowerPanel(Color.DarkRed)
        pnlTowerYellow = CreateTowerPanel(Color.Goldenrod)
        pnlTowerGreen = CreateTowerPanel(Color.DarkGreen)
        towerFlow.Controls.AddRange({pnlTowerRed, pnlTowerYellow, pnlTowerGreen})
        leftTlp.Controls.Add(towerFlow, 0, 1)

        dgvHistory = New DataGridView With {
            .Dock = DockStyle.Fill, .BackgroundColor = Color.FromArgb(40, 40, 40), .ForeColor = Color.White,
            .RowHeadersVisible = False, .AllowUserToAddRows = False, .ReadOnly = True,
            .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, .SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            .BorderStyle = BorderStyle.None, .DefaultCellStyle = New DataGridViewCellStyle With {.BackColor = Color.FromArgb(50, 50, 50), .Font = New Font("Segoe UI", 12)},
            .ColumnHeadersDefaultCellStyle = New DataGridViewCellStyle With {.BackColor = Color.FromArgb(30, 30, 30), .ForeColor = Color.White, .Font = New Font("Segoe UI", 12, FontStyle.Bold)}
        }
        dgvHistory.Columns.Add("Time", "Time")
        dgvHistory.Columns.Add("Barcode", "Barcode")
        dgvHistory.Columns.Add("Result", "Result")
        leftTlp.Controls.Add(dgvHistory, 0, 2)
        mainTlp.Controls.Add(leftTlp, 0, 0)

        ' 2B. Center Panel (Camera & Info Text)
        Dim centerTlp = New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 3}
        centerTlp.RowStyles.Add(New RowStyle(SizeType.Absolute, 80)) ' Scanned Model
        centerTlp.RowStyles.Add(New RowStyle(SizeType.Absolute, 80)) ' Vision Result
        centerTlp.RowStyles.Add(New RowStyle(SizeType.Percent, 100)) ' Camera
        
        txtScannedModel = CreateDataTextBox(Color.Lime, "MODEL: NONE")
        txtVisionResult = CreateDataTextBox(Color.White, "READY")
        
        Dim lblCam = New Label With {.Text = "LIVE VISION PREVIEW", .ForeColor = Color.Silver, .Font = New Font("Segoe UI", 12), .AutoSize = True, .Margin = New Padding(0, 10, 0, 5)}
        picCamera = New PictureBox With {.Dock = DockStyle.Fill, .BackColor = Color.Black, .SizeMode = PictureBoxSizeMode.Zoom, .BorderStyle = BorderStyle.FixedSingle, .Margin = New Padding(0, 0, 0, 10)}
        
        Dim camFlow = New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 2}
        camFlow.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        camFlow.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        camFlow.Controls.Add(lblCam, 0, 0)
        camFlow.Controls.Add(picCamera, 0, 1)

        centerTlp.Controls.Add(txtScannedModel, 0, 0)
        centerTlp.Controls.Add(txtVisionResult, 0, 1)
        centerTlp.Controls.Add(camFlow, 0, 2)
        mainTlp.Controls.Add(centerTlp, 1, 0)

        ' 2C. Right Panel (Stats & Program)
        Dim rightTlp = New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 6}
        rightTlp.RowStyles.Add(New RowStyle(SizeType.Absolute, 40))  ' Prog Label
        rightTlp.RowStyles.Add(New RowStyle(SizeType.Absolute, 60))  ' Prog Combo
        rightTlp.RowStyles.Add(New RowStyle(SizeType.Absolute, 60))  ' Pass
        rightTlp.RowStyles.Add(New RowStyle(SizeType.Absolute, 60))  ' Fail
        rightTlp.RowStyles.Add(New RowStyle(SizeType.Absolute, 60))  ' Cycle Time
        rightTlp.RowStyles.Add(New RowStyle(SizeType.Percent, 100))  ' Hardware Status

        Dim lblProg = New Label With {.Text = "ROBOT PROGRAM", .ForeColor = Color.Silver, .Font = New Font("Segoe UI", 12), .AutoSize = True, .Margin = New Padding(20, 10, 0, 5)}
        cmbProgramSelect = New ComboBox With {.Font = New Font("Segoe UI", 16), .DropDownStyle = ComboBoxStyle.DropDownList, .BackColor = Color.FromArgb(40, 40, 40), .ForeColor = Color.White, .Margin = New Padding(20, 0, 0, 20), .Width = 280}
        
        If config.ProgramNames Is Nothing OrElse config.ProgramNames.Length < 15 Then
            config.ProgramNames = {"Program 1", "Program 2", "Program 3", "Program 4", "Program 5", "Program 6", "Program 7", "Program 8", "Program 9", "Program 10", "Program 11", "Program 12", "Program 13", "Program 14", "Program 15"}
        End If
        
        For i As Integer = 0 To 14
            cmbProgramSelect.Items.Add(config.ProgramNames(i))
        Next
        cmbProgramSelect.SelectedIndex = 0

        lblPassCount = CreateStatLabel("PASS: " & config.PassCount, Color.Lime)
        lblFailCount = CreateStatLabel("FAIL: " & config.FailCount, Color.Red)
        lblCycleTime = CreateStatLabel("LAST: 0.0s", Color.Yellow)

        Dim hwFlow = New FlowLayoutPanel With {.Dock = DockStyle.Fill, .FlowDirection = FlowDirection.TopDown, .Margin = New Padding(20, 20, 0, 0)}
        Dim hwLabel = New Label With {.Text = "HARDWARE STATUS", .ForeColor = Color.Silver, .Font = New Font("Segoe UI", 12), .AutoSize = True, .Margin = New Padding(0, 0, 0, 10)}
        hwFlow.Controls.Add(hwLabel)
        picStatusModbus = CreateStatusIndicator("MODBUS TCP I/O", hwFlow)
        picStatusVision = CreateStatusIndicator("COGNEX VISION", hwFlow)
        picStatusScanner = CreateStatusIndicator("KEYENCE SCANNER", hwFlow)

        rightTlp.Controls.Add(lblProg, 0, 0)
        rightTlp.Controls.Add(cmbProgramSelect, 0, 1)
        rightTlp.Controls.Add(lblPassCount, 0, 2)
        rightTlp.Controls.Add(lblFailCount, 0, 3)
        rightTlp.Controls.Add(lblCycleTime, 0, 4)
        rightTlp.Controls.Add(hwFlow, 0, 5)
        mainTlp.Controls.Add(rightTlp, 2, 0)

        ' 3. Footer (Log + Buttons)
        Dim footerTlp = New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 2, .RowCount = 1, .Margin = New Padding(0, 10, 0, 0)}
        footerTlp.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 60)) ' Log
        footerTlp.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 40)) ' Buttons

        lstLog = New ListBox With {.Dock = DockStyle.Fill, .BackColor = Color.Black, .ForeColor = Color.LimeGreen, .Font = New Font("Consolas", 11), .BorderStyle = BorderStyle.FixedSingle, .Margin = New Padding(0)}
        footerTlp.Controls.Add(lstLog, 0, 0)

        Dim btnFlow = New FlowLayoutPanel With {.Dock = DockStyle.Fill, .FlowDirection = FlowDirection.LeftToRight, .Padding = New Padding(10, 0, 0, 0)}
        btnStart = CreateIndustrialButton("START [F1]", Color.ForestGreen, 120, 120)
        btnReset = CreateIndustrialButton("RESET [F2]", Color.RoyalBlue, 120, 120)
        btnPause = CreateIndustrialButton("PAUSE [F3]", Color.Orange, 120, 120) 
        btnSettings = CreateIndustrialButton("CONFIG [F5]", Color.Gray, 120, 120)
        btnFlow.Controls.AddRange({btnStart, btnReset, btnPause, btnSettings})
        footerTlp.Controls.Add(btnFlow, 1, 0)
        rootTlp.Controls.Add(footerTlp, 0, 2)

        Me.Controls.Add(rootTlp)

        tmrMainLoop = New Timer With {.Interval = 50, .Enabled = True}
        AddHandler tmrMainLoop.Tick, AddressOf tmrMainLoop_Tick
        
        tmrCamera = New Timer With {.Interval = 500, .Enabled = True}
        AddHandler tmrCamera.Tick, AddressOf tmrCamera_Tick
        AddHandler btnStart.Click, AddressOf btnStart_Click
        AddHandler btnReset.Click, AddressOf btnReset_Click
        AddHandler btnPause.Click, AddressOf btnPause_Click
        AddHandler btnSettings.Click, AddressOf btnSettings_Click
        AddHandler btnEStop.Click, AddressOf btnEStop_Click
    End Sub

    ' UI Helpers
    Private Function CreateStatusIndicator(text As String, parent As Control) As Panel
        Dim rowPnl = New Panel With {.Width = 300, .Height = 35, .Margin = New Padding(0, 5, 0, 5)}
        Dim p = New Panel With {.Size = New Size(24, 24), .Location = New Point(0, 5), .BackColor = Color.DarkGray, .BorderStyle = BorderStyle.FixedSingle}
        Dim gp As New Drawing2D.GraphicsPath() : gp.AddEllipse(0, 0, 24, 24) : p.Region = New Region(gp)
        Dim lbl = New Label With {.Text = text, .ForeColor = Color.Silver, .Font = New Font("Segoe UI", 12), .Location = New Point(35, 5), .AutoSize = True}
        rowPnl.Controls.AddRange({p, lbl})
        parent.Controls.Add(rowPnl)
        Return p
    End Function

    Private Function CreateTowerPanel(c As Color) As Panel
        Dim p As New Panel With {.Size = New Size(60, 60), .Margin = New Padding(5, 0, 5, 0), .BackColor = c, .BorderStyle = BorderStyle.FixedSingle}
        Dim gp As New Drawing2D.GraphicsPath() : gp.AddEllipse(0, 0, 60, 60) : p.Region = New Region(gp)
        Return p
    End Function

    Private Function CreateIndustrialButton(t As String, c As Color, w As Integer, h As Integer) As Button
        Return New Button With {.Text = t, .BackColor = c, .ForeColor = Color.White, .Font = New Font("Segoe UI", 12, FontStyle.Bold), .Size = New Size(w, h), .Margin = New Padding(5), .FlatStyle = FlatStyle.Flat}
    End Function

    Private Function CreateStatLabel(t As String, c As Color) As Label
        Return New Label With {.Text = t, .ForeColor = c, .Font = New Font("Segoe UI", 26, FontStyle.Bold), .Dock = DockStyle.Fill, .TextAlign = ContentAlignment.MiddleLeft, .Margin = New Padding(20, 0, 0, 0)}
    End Function

    Private Function CreateDataTextBox(c As Color, prompt As String) As TextBox
        Return New TextBox With {.BackColor = Color.FromArgb(20, 20, 20), .ForeColor = c, .Font = New Font("Consolas", 28, FontStyle.Bold), .Dock = DockStyle.Fill, .ReadOnly = True, .TextAlign = HorizontalAlignment.Center, .BorderStyle = BorderStyle.FixedSingle, .Text = prompt, .Margin = New Padding(0, 0, 0, 10)}
    End Function
#End Region

#Region "Form Lifecycle"
    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        InitializeHardwareAsync()
    End Sub

    Private Async Sub InitializeHardwareAsync()
        Log("Connecting to Modbus at " & config.ModbusIP & "...")
        Await Task.Run(Sub()
            Try
                modbusClient = New ModbusClient(config.ModbusIP, config.ModbusPort)
                modbusClient.Connect()
                Log("SUCCESS: Modbus Initialized.")
            Catch ex As Exception
                Log("ERROR: Modbus Fail: " & ex.Message)
            End Try
        End Sub)
    End Sub
#End Region

#Region "Main Control Loop"
    Private Sub tmrCamera_Tick(sender As Object, e As EventArgs)
        UpdateCameraPreviewAsync()
    End Sub

    Private Async Sub UpdateCameraPreviewAsync()
        If picCamera Is Nothing OrElse picCamera.IsDisposed Then Return
        
        Try
            Using client As New System.Net.Http.HttpClient()
                client.Timeout = TimeSpan.FromMilliseconds(500)
                Dim bytes = Await client.GetByteArrayAsync(config.CameraUrl)
                If bytes IsNot Nothing AndAlso bytes.Length > 0 Then
                    Using ms As New IO.MemoryStream(bytes)
                        Dim img = Image.FromStream(ms)
                        picCamera.Invoke(Sub() 
                            If picCamera.Image IsNot Nothing Then picCamera.Image.Dispose()
                            picCamera.Image = img
                        End Sub)
                    End Using
                End If
            End Using
        Catch 
            ' Silent fail for preview to avoid spamming
        End Try
    End Sub

    Private Async Sub tmrMainLoop_Tick(sender As Object, e As EventArgs)
        If lblClock IsNot Nothing AndAlso lblClock.IsHandleCreated Then
            lblClock.Invoke(Sub() lblClock.Text = DateTime.Now.ToString("HH:mm:ss"))
        End If
        
        blinkCounter += 1
        If blinkCounter >= 10 Then
            blinkState = Not blinkState
            blinkCounter = 0
        End If

        If (modbusClient IsNot Nothing AndAlso modbusClient.Connected) Then
            Try
                inputs = Await Task.Run(Function() modbusClient.ReadDiscreteInputs(0, 16))
                
                Dim checkCurtain = (currentState = MachineStatus.B1_DISPENSE_START OrElse currentState = MachineStatus.B1_DISPENSE_WAIT)
                If (Not inputs(0)) Or (Not inputs(2) AndAlso checkCurtain) Or (Not inputs(6) AndAlso currentState <> MachineStatus.IDLE) Then
                    TriggerEmergencyStop("HARDWARE INTERRUPTION DETECTED")
                End If

                If Not isEStopActive AndAlso Not isPaused Then
                    Await ExecuteStateLogicAsync()
                End If

                If currentBlinkColor = BlinkColor.YELLOW Then outputs(1) = blinkState
                If currentBlinkColor = BlinkColor.GREEN Then outputs(2) = blinkState

                outputs(5) = isPaused
                outputs(6) = isEStopActive 
                
                ' Apply Program Selection Bitwise (DO 07-10)
                ApplyProgramBits()

                Await Task.Run(Sub() modbusClient.WriteMultipleCoils(0, outputs))
            Catch ex As Exception
                Log("SCAN FAULT: " & ex.Message)
            End Try
        End If
        RefreshDashboard()
    End Sub

    Private Sub ApplyProgramBits()
        Dim progNum = cmbProgramSelect.SelectedIndex + 1 ' 1-15
        ' Map 4 bits to DO 07, 08, 09, 10
        outputs(7) = (progNum And &H1) <> 0
        outputs(8) = (progNum And &H2) <> 0
        outputs(9) = (progNum And &H4) <> 0
        outputs(10) = (progNum And &H8) <> 0
    End Sub
#End Region

#Region "Core State Machine"
    Private Async Function ExecuteStateLogicAsync() As Task
        Select Case currentState
            Case MachineStatus.IDLE
                ' Reset outputs except Program select
                outputs(0) = False : outputs(1) = False : outputs(2) = False
                outputs(4) = False : outputs(5) = False : outputs(10) = False
                currentBlinkColor = BlinkColor.NONE
                If inputs(1) Then currentState = MachineStatus.A1_WAIT_RETRACT

            Case MachineStatus.A1_WAIT_RETRACT
                If inputs(3) Then ' Cylinder Retract Sensor Active
                    currentState = MachineStatus.A1_READY
                End If

            Case MachineStatus.A1_READY
                outputs(0) = False ' Red Off
                outputs(2) = False ' Green Off
                outputs(1) = True  ' Yellow Light On
                currentBlinkColor = BlinkColor.NONE
                currentState = MachineStatus.A1_BARCODE_SCAN

            Case MachineStatus.A1_BARCODE_SCAN
                Dim barcode = Await SendTcpRequest(config.KeyenceIP, config.KeyencePort, "LON" & vbCr)
                If Not String.IsNullOrEmpty(barcode) AndAlso Not barcode.Contains("ERROR") Then
                    barcode = barcode.Trim()
                    txtScannedModel.Invoke(Sub() txtScannedModel.Text = barcode)
                    If barcode = config.MasterBarcode Then
                        currentState = MachineStatus.B1_READY
                    Else
                        currentState = MachineStatus.WRONG_MODEL
                    End If
                Else
                    Await Task.Delay(500) ' Wait and retry
                End If

            Case MachineStatus.WRONG_MODEL
                outputs(0) = True ' Red Light On
                outputs(1) = False
                outputs(2) = False
                outputs(10) = False ' Cylinder Retract (Unclamp)
                currentBlinkColor = BlinkColor.NONE
                
                isPaused = True
                MessageBox.Show("Wrong PWBA Model: " & txtScannedModel.Text & vbCrLf & "Expected: " & config.MasterBarcode, "WRONG PWBA", MessageBoxButtons.OK, MessageBoxIcon.Error)
                isPaused = False
                txtScannedModel.Invoke(Sub() txtScannedModel.Text = "MODEL: NONE")
                currentState = MachineStatus.A1_READY

            Case MachineStatus.B1_READY
                outputs(0) = False 
                outputs(2) = False
                currentBlinkColor = BlinkColor.YELLOW ' Yellow blink while waiting for Start
                
                If inputs(1) AndAlso inputs(2) Then ' Start pressed AND Curtain sensor active
                    currentBlinkColor = BlinkColor.NONE
                    cycleStartTime = DateTime.Now
                    currentState = MachineStatus.B1_DISPENSE_START
                End If

            Case MachineStatus.B1_DISPENSE_START
                outputs(1) = True  ' Yellow Light On (In-Process)
                outputs(4) = True  ' Robot Start Signal (DO 04)
                Await Task.Delay(500) ' Pulse
                outputs(4) = False
                currentState = MachineStatus.B1_DISPENSE_WAIT

            Case MachineStatus.B1_DISPENSE_WAIT
                If inputs(4) Then ' Robot Complete Signal (DI 04)
                    currentState = MachineStatus.B1_DISPENSE_POST
                End If
                If inputs(5) Then currentState = MachineStatus.ALARM ' Robot Fail/Fault

            Case MachineStatus.B1_DISPENSE_POST
                outputs(1) = False ' Yellow Light Off
                currentState = MachineStatus.VISION_CHECK

            Case MachineStatus.VISION_CHECK
                Dim vPath = Await SendTcpRequest(config.CognexIP, config.CognexPort, "T" & vbCr)
                Dim isPass = vPath.Contains("OK") Or inputs(6) ' TCP OK or DI 06
                
                If isPass Then
                    currentState = MachineStatus.FINISH_WAIT_ROBOT
                    robotWaitStart = DateTime.Now
                Else
                    currentState = MachineStatus.ALARM
                End If

            Case MachineStatus.FINISH_WAIT_ROBOT
                currentBlinkColor = BlinkColor.GREEN
                ' Wait 1.5s for Robot to retreat to Home position 
                If (DateTime.Now - robotWaitStart).TotalSeconds >= 1.5 Then
                    currentState = MachineStatus.FINISH_SUCCESS
                End If

            Case MachineStatus.FINISH_SUCCESS
                currentBlinkColor = BlinkColor.GREEN
                outputs(10) = True ' Cylinder Retract & Unclamp (DO 10)
                If inputs(3) Then  ' Wait for Cylinder Retract Sensor
                    Dim duration = (DateTime.Now - cycleStartTime).TotalSeconds
                    Dim model = txtScannedModel.Text
                    Dim ts = DateTime.Now.ToString("HH:mm:ss")
                    
                    If lblPassCount IsNot Nothing Then
                        config.PassCount += 1
                        SaveSettings()
                    End If

                    If dgvHistory IsNot Nothing AndAlso dgvHistory.IsHandleCreated Then
                        dgvHistory.Invoke(Sub() 
                            dgvHistory.Rows.Insert(0, ts, model, "PASS")
                            If dgvHistory.Rows.Count > 50 Then dgvHistory.Rows.RemoveAt(50)
                        End Sub)
                    End If

                    LogToCsv(model, "PASS", duration)
                    currentState = MachineStatus.IDLE
                End If

            Case MachineStatus.ALARM
                outputs(0) = True ' Red On
                outputs(2) = False
                currentBlinkColor = BlinkColor.NONE
                ' Wait for reset
        End Select
    End Function
# End Region

#Region "Industrial Utils"
    Private Async Function SendTcpRequest(ip As String, port As Integer, cmd As String) As Task(Of String)
        Using c As New TcpClient()
            Try
                Dim ct = c.ConnectAsync(ip, port)
                If Await Task.WhenAny(ct, Task.Delay(2000)) IsNot ct Then return "ERROR"
                Dim s = c.GetStream()
                Dim d = Encoding.ASCII.GetBytes(cmd)
                Await s.WriteAsync(d, 0, d.Length)
                Dim b(512) As Byte
                Dim read = Await s.ReadAsync(b, 0, b.Length)
                return Encoding.ASCII.GetString(b, 0, read)
            Catch : return "ERROR" : End Try
        End Using
    End Function

    Private Sub TriggerEmergencyStop(reason As String)
        isEStopActive = True
        Array.Clear(outputs, 0, 11) ' Reset DO 00-10
        outputs(0) = True ' Red Light On (DO 00)
        outputs(3) = True ' Robot Emergency Stop Output (DO 03)
        currentState = MachineStatus.ALARM
        Log("EMERGENCY STOP: " & reason)
        MessageBox.Show(reason, "SYSTEM SAFETY FAULT", MessageBoxButtons.OK, MessageBoxIcon.Hand)
    End Sub

    Private Sub RefreshDashboard()
        If lblMachineState IsNot Nothing AndAlso lblMachineState.IsHandleCreated Then
            lblMachineState.Invoke(Sub()
                lblMachineState.Text = If(isPaused, "PAUSED", currentState.ToString().Replace("_", " "))
                lblMachineState.BackColor = If(isPaused, Color.Orange, If(currentState = MachineStatus.ALARM, Color.DarkRed, Color.FromArgb(45, 45, 45)))
            End Sub)
        End If
        
        If lblPassCount IsNot Nothing AndAlso lblPassCount.IsHandleCreated Then
            lblPassCount.Invoke(Sub() lblPassCount.Text = "PASS: " & config.PassCount)
        End If
        
        If lblFailCount IsNot Nothing AndAlso lblFailCount.IsHandleCreated Then
            lblFailCount.Invoke(Sub() lblFailCount.Text = "FAIL: " & config.FailCount)
        End If

        ' Hardware Status Indicators (Strict Alignment with PC I/O Table)
        UpdateHardwareHealth()

        ' Tower Light Visualization (Flowchart Logic)
        If pnlTowerRed IsNot Nothing Then 
            pnlTowerRed.BackColor = If(outputs(0) OrElse isEStopActive, Color.Red, Color.DarkRed)
        End If
        
        If pnlTowerYellow IsNot Nothing Then
            pnlTowerYellow.BackColor = If(outputs(1), Color.Yellow, Color.Goldenrod)
        End If

        If pnlTowerGreen IsNot Nothing Then
            pnlTowerGreen.BackColor = If(outputs(2), Color.Lime, Color.DarkGreen)
        End If
    End Sub

    Private Sub UpdateHardwareHealth()
        If picStatusModbus IsNot Nothing Then 
            picStatusModbus.BackColor = If(modbusClient IsNot Nothing AndAlso modbusClient.Connected, Color.Lime, Color.Red)
        End If
        ' Based on table DI 04 (Robot), DI 06/07 (Vision), DI 01 (Scanner)
        picStatusVision.BackColor = If(inputs(6) OrElse txtVisionResult.Text = "PASS", Color.Lime, Color.Red)
        picStatusScanner.BackColor = If(txtScannedModel.Text <> "ERROR" AndAlso txtScannedModel.Text <> "MODEL: NONE", Color.Lime, Color.Red)
    End Sub

    Private Sub Log(msg As String)
        Try
            Dim stamp = DateTime.Now.ToString("HH:mm:ss")
            Dim fullMsg = "[" & stamp & "] " & If(msg, "")
            
            ' Null Check & Handle Check for lstLog (Startup Safety)
            If lstLog IsNot Nothing AndAlso lstLog.IsHandleCreated AndAlso Not lstLog.IsDisposed Then
                If lstLog.InvokeRequired Then
                    lstLog.Invoke(Sub() 
                        lstLog.Items.Insert(0, fullMsg)
                        If lstLog.Items.Count > 100 Then lstLog.Items.RemoveAt(100)
                    End Sub)
                Else
                    lstLog.Items.Insert(0, fullMsg)
                    If lstLog.Items.Count > 100 Then lstLog.Items.RemoveAt(100)
                End If
            End If
            
            ' Always log to debug/console
            Console.WriteLine(fullMsg)
        Catch ex As Exception
            ' Silent fail to prevent taking down the constructor
        End Try
    End Sub

    Private Sub LogToCsv(model As String, res As String, dur As Double)
        Try
            Dim line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{model.Replace(",", " ")},{res},{dur:F2}"
            IO.File.AppendAllText(logPath, line & vbCrLf)
        Catch : End Try
    End Sub
#End Region

#Region "HMI Interaction"
    Private Sub btnStart_Click(sender As Object, e As EventArgs)
        If isEStopActive Then Return
        If isPaused Then : isPaused = False : Return : End If
        
        If currentState = MachineStatus.B1_READY Then 
            If inputs(2) Then ' Curtain sensor check
                currentBlinkColor = BlinkColor.NONE
                cycleStartTime = DateTime.Now
                currentState = MachineStatus.B1_DISPENSE_START
            Else
                MessageBox.Show("Curtain Sensor is blocked! Clear curtain to start.", "SAFETY INTERLOCK", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            End If
        ElseIf currentState = MachineStatus.IDLE Or currentState = MachineStatus.ALARM Then
            currentState = MachineStatus.A1_WAIT_RETRACT
        End If
    End Sub

    Private Sub btnReset_Click(sender As Object, e As EventArgs)
        isEStopActive = False : isPaused = False : currentBlinkColor = BlinkColor.NONE
        txtScannedModel.Text = "MODEL: NONE" : txtVisionResult.Text = "READY"
        ' Corrected IDLE behavior: Clear outputs and return to A1 sequence
        Array.Clear(outputs, 0, 11)
        currentState = MachineStatus.A1_WAIT_RETRACT
    End Sub

    Private Sub btnPause_Click(sender As Object, e As EventArgs)
        If Not isEStopActive Then isPaused = Not isPaused
    End Sub

    Private Sub btnSettings_Click(sender As Object, e As EventArgs)
        If pnlSettingsView.Visible Then
            ' Return to dashboard if already open
            pnlSettingsView.Visible = False
            pnlMainDashboard.Visible = True
        Else
            ' Load current values and show settings
            LoadSettingsToUI()
            pnlMainDashboard.Visible = False
            pnlSettingsView.Visible = True
        End If
    End Sub

    Private Sub LoadSettingsToUI()
        txtCfgModbusIP.Text = config.ModbusIP
        txtCfgModbusPort.Text = config.ModbusPort.ToString()
        txtCfgVisionIP.Text = config.CognexIP
        txtCfgVisionPort.Text = config.CognexPort.ToString()
        txtCfgScannerIP.Text = config.KeyenceIP
        txtCfgScannerPort.Text = config.KeyencePort.ToString()
        txtCfgCameraUrl.Text = config.CameraUrl
        txtCfgMasterBarcode.Text = config.MasterBarcode
        If config.ProgramNames IsNot Nothing Then
            txtCfgProgramNames.Text = String.Join(vbCrLf, config.ProgramNames)
        End If
    End Sub

    Private Sub btnCfgSave_Click(sender As Object, e As EventArgs)
        Try
            config.ModbusIP = txtCfgModbusIP.Text
            config.ModbusPort = Integer.Parse(txtCfgModbusPort.Text)
            config.CognexIP = txtCfgVisionIP.Text
            config.CognexPort = Integer.Parse(txtCfgVisionPort.Text)
            config.KeyenceIP = txtCfgScannerIP.Text
            config.KeyencePort = Integer.Parse(txtCfgScannerPort.Text)
            config.CameraUrl = txtCfgCameraUrl.Text
            config.MasterBarcode = txtCfgMasterBarcode.Text
            
            Dim lines = txtCfgProgramNames.Text.Split(New String() {vbCrLf, vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries)
            Dim newProgs As New System.Collections.Generic.List(Of String)
            For i As Integer = 0 To 14
                If i < lines.Length Then : newProgs.Add(lines(i).Trim()) : Else : newProgs.Add("Program " & (i + 1)) : End If
            Next
            config.ProgramNames = newProgs.ToArray()
            
            SaveSettings()
            Log("CONFIG SAVED. Applying changes...")
            
            ' Update UI Dropdown with new names
            cmbProgramSelect.Items.Clear()
            For Each p In config.ProgramNames : cmbProgramSelect.Items.Add(p) : Next
            cmbProgramSelect.SelectedIndex = 0
            
            ' Force Reconnect
            InitializeHardwareAsync()
            
            ' Switch back
            pnlSettingsView.Visible = False
            pnlMainDashboard.Visible = True
        Catch ex As Exception
            MessageBox.Show("Invalid Input: " & ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub

    Private Sub SetupSettingsPanel()
        Dim tlp = New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 2, .RowCount = 1, .BackColor = Color.FromArgb(40, 40, 40), .Padding = New Padding(40)}
        tlp.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
        tlp.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
        pnlSettingsView.Controls.Add(tlp)

        Dim leftFlow = New FlowLayoutPanel With {.Dock = DockStyle.Fill, .FlowDirection = FlowDirection.TopDown, .AutoScroll = True}
        Dim rightFlow = New FlowLayoutPanel With {.Dock = DockStyle.Fill, .FlowDirection = FlowDirection.TopDown}
        tlp.Controls.Add(leftFlow, 0, 0)
        tlp.Controls.Add(rightFlow, 1, 0)

        ' Headers
        Dim CreateHeader = Function(title As String) New Label With {.Text = title, .ForeColor = Color.Cyan, .Font = New Font("Segoe UI", 18, FontStyle.Bold), .AutoSize = True, .Margin = New Padding(0, 0, 0, 20)}
        Dim CreateLabel = Function(title As String) New Label With {.Text = title, .ForeColor = Color.Silver, .Font = New Font("Segoe UI", 12), .AutoSize = True}
        Dim CreateEdit = Function() New TextBox With {.BackColor = Color.FromArgb(30, 30, 30), .ForeColor = Color.White, .BorderStyle = BorderStyle.FixedSingle, .Font = New Font("Segoe UI", 14), .Width = 400, .Margin = New Padding(0, 5, 0, 20)}

        leftFlow.Controls.Add(CreateHeader("HARDWARE CONFIGURATION"))
        leftFlow.Controls.Add(CreateLabel("Modbus TCP I/O IP:"))
        txtCfgModbusIP = CreateEdit() : leftFlow.Controls.Add(txtCfgModbusIP)
        leftFlow.Controls.Add(CreateLabel("Modbus Port:"))
        txtCfgModbusPort = CreateEdit() : leftFlow.Controls.Add(txtCfgModbusPort)
        
        leftFlow.Controls.Add(CreateLabel("Cognex Vision IP:"))
        txtCfgVisionIP = CreateEdit() : leftFlow.Controls.Add(txtCfgVisionIP)
        leftFlow.Controls.Add(CreateLabel("Cognex Port:"))
        txtCfgVisionPort = CreateEdit() : leftFlow.Controls.Add(txtCfgVisionPort)

        leftFlow.Controls.Add(CreateLabel("Keyence Scanner IP:"))
        txtCfgScannerIP = CreateEdit() : leftFlow.Controls.Add(txtCfgScannerIP)
        leftFlow.Controls.Add(CreateLabel("Scanner Port:"))
        txtCfgScannerPort = CreateEdit() : leftFlow.Controls.Add(txtCfgScannerPort)
        
        leftFlow.Controls.Add(CreateLabel("Camera Stream URL (MJPEG/JPG):"))
        txtCfgCameraUrl = CreateEdit() : leftFlow.Controls.Add(txtCfgCameraUrl)

        leftFlow.Controls.Add(CreateLabel("Master Barcode (Expected Model):"))
        txtCfgMasterBarcode = CreateEdit() : leftFlow.Controls.Add(txtCfgMasterBarcode)

        rightFlow.Controls.Add(CreateHeader("ROBOT PROGRAMS"))
        rightFlow.Controls.Add(CreateLabel("Enter 15 Program Names (One per line):"))
        txtCfgProgramNames = New TextBox With {.Multiline = True, .ScrollBars = ScrollBars.Vertical, .Size = New Size(450, 400), .BackColor = Color.FromArgb(30,30,30), .ForeColor = Color.White, .BorderStyle = BorderStyle.FixedSingle, .Font = New Font("Consolas", 12), .Margin = New Padding(0, 10, 0, 20)}
        rightFlow.Controls.Add(txtCfgProgramNames)

        Dim btnPnl = New FlowLayoutPanel With {.AutoSize = True, .FlowDirection = FlowDirection.LeftToRight}
        btnCfgSave = CreateIndustrialButton("SAVE & APPLY", Color.ForestGreen, 200, 60)
        btnCfgCancel = CreateIndustrialButton("CANCEL", Color.DimGray, 150, 60)
        AddHandler btnCfgSave.Click, AddressOf btnCfgSave_Click
        AddHandler btnCfgCancel.Click, Sub() 
            pnlSettingsView.Visible = False
            pnlMainDashboard.Visible = True
        End Sub
        btnPnl.Controls.AddRange({btnCfgSave, btnCfgCancel})
        rightFlow.Controls.Add(btnPnl)
    End Sub

    Private Sub btnEStop_Click(sender As Object, e As EventArgs)
        If MessageBox.Show("CLOSE THE SYSTEM?", "EXIT CONFIRM", MessageBoxButtons.YesNo) = DialogResult.Yes Then
            Application.Exit()
        End If
    End Sub
#End Region




End Class


