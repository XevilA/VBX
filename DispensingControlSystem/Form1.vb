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
    Public    Enum MachineStatus
        IDLE                ' Green Light ON, Wait Start
        A1_CLAMPING         ' DO 10 ON, Wait delay
        A1_BARCODE_SCAN     ' Yellow Light ON, TCP 'LON', Read Barcode
        A1_MODEL_CONFIRM    ' Wait Start pulse + Check Curtain + Yellow Blink
        B1_DISPENSE_START   ' DO 04 Pulse
        B1_DISPENSE_WAIT    ' Wait for Robot Complete (DI 04)
        B1_DISPENSE_POST    ' Yellow Off, Green BLINK
        B1_VISION_CHECK     ' TCP 'T', Read Result
        B1_FINISH_SUCCESS   ' DO 10 OFF, Wait DI 03 Retract, Green ON, Log data
        D1_ERROR_PATH       ' Red Light ON, Show Pop, Wait Start
        A1_RETRACT_PATH     ' DO 10 OFF, Wait DI 03 Retract, Red OFF, Green ON
        ALARM               ' System Fault
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
    
    ' I/O Mapping - AMSAMOTION MT3A-IO1632 (PC I/O Table)
    ' === INPUTS (DI) ===
    ' DI 00: E-Stop Safety (NC) - must be HIGH (True) = safe. LOW (False) = E-Stop!
    ' DI 01: Start Button
    ' DI 02: Safety Light Curtain (NC) - must be HIGH (True) = clear. LOW = tripped!
    ' DI 03: Cylinder Retract Sensor (at Home position)
    ' DI 04: Robot Complete signal
    ' DI 05: Robot Fault signal
    ' DI 06: Vision OK (PASS from Cognex)
    ' DI 07: Vision NG (FAIL from Cognex)
    ' === OUTPUTS (DO) ===
    ' DO 00: Tower Light Red  (ALARM / E-Stop)
    ' DO 01: Tower Light Yellow (Robot in progress)
    ' DO 02: Tower Light Green  (Ready / Finish)
    ' DO 03: Robot E-Stop signal (ON = cut power to robot)
    ' DO 04: Robot Start pulse (500ms)
    ' DO 05: Robot Pause
    ' DO 06: Program Select Bit 0 (LSB)
    ' DO 07: Program Select Bit 1
    ' DO 08: Program Select Bit 2
    ' DO 09: Program Select Bit 3 (MSB) — supports up to 15 programs
    ' DO 10: Cylinder Clamp/Unclamp solenoid
    
    Private isEStopActive As Boolean = False
    Private isPaused As Boolean = False
    Private isModbusOK As Boolean = False
    Private isCognexOK As Boolean = False
    Private isKeyenceOK As Boolean = False
    Private ReadOnly logPath As String = IO.Path.Combine(Application.StartupPath, "ProductionLog.csv")
    
    ' Integrated Config Controls
    Private pnlMainDashboard As Panel
    Private pnlSettingsView As Panel
    Private txtCfgModbusIP, txtCfgModbusPort, txtCfgVisionIP, txtCfgVisionPort, txtCfgScannerIP, txtCfgScannerPort, txtCfgCameraUrl, txtCfgMasterBarcode, txtCfgProgramNames As TextBox
    Private btnCfgSave, btnCfgCancel, btnCfgScan As Button
    Private isShowingEStopPopup As Boolean = False
    Private isTimerBusy As Boolean = False  ' Prevent timer re-entrancy

    ' Simple class to hold LAN scan result (avoids tuple compatibility issues)
    Private Class DeviceFound
        Public Property IP As String
        Public Property DeviceType As String
        Public Property Port As Integer
        Public Sub New(ip As String, dtype As String, p As Integer)
            Me.IP = ip
            Me.DeviceType = dtype
            Me.Port = p
        End Sub
    End Class
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
        Me.KeyPreview = True
        AddHandler Me.KeyDown, AddressOf Form1_KeyDown
        
        ' Set app icon from logo.jpg if it exists
        Try
            Dim logoPath = IO.Path.Combine(Application.StartupPath, "logo.jpg")
            If Not IO.File.Exists(logoPath) Then
                logoPath = "C:\Users\Administrator\Documents\VBX\logo.jpg"
            End If
            If IO.File.Exists(logoPath) Then
                Using bmp As New Bitmap(logoPath)
                    Me.Icon = Icon.FromHandle(bmp.GetHicon())
                End Using
            End If
        Catch : End Try
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
        
        ' Health probe every 5 seconds to check Cognex & Keyence reachability
        Dim tmrProbe = New Timer With {.Interval = 5000, .Enabled = True}
        AddHandler tmrProbe.Tick, Sub() ProbeDeviceHealthAsync()
        
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
        
        Dim candidateUrls() As String = {
            config.CameraUrl,
            $"http://{config.CognexIP}/snapshot.jpg",
            $"http://{config.CognexIP}/img/snapshot.jpg",
            $"http://{config.CognexIP}/insightSS.jpg",
            $"http://{config.CognexIP}/image.bmp",
            $"http://{config.CognexIP}/GetBitmap"
        }
        
        For Each url In candidateUrls
            Try
                Using client As New System.Net.Http.HttpClient()
                    client.Timeout = TimeSpan.FromMilliseconds(800)
                    Dim bytes = Await client.GetByteArrayAsync(url)
                    If bytes IsNot Nothing AndAlso bytes.Length > 1000 Then  ' > 1KB = real image
                        Using ms As New IO.MemoryStream(bytes)
                            Dim img = Image.FromStream(ms)
                            picCamera.Invoke(Sub()
                                If picCamera.Image IsNot Nothing Then picCamera.Image.Dispose()
                                picCamera.Image = img
                            End Sub)
                        End Using
                        ' Update config URL if we found a working one
                        If url <> config.CameraUrl Then
                            config.CameraUrl = url
                            SaveSettings()
                            Log("Camera URL auto-detected: " & url)
                        End If
                        Return  ' Success - stop trying
                    End If
                End Using
            Catch
                ' Try next URL
            End Try
        Next
    End Sub

    Private Async Sub tmrMainLoop_Tick(sender As Object, e As EventArgs)
        ' Guard against re-entrant async calls
        If isTimerBusy Then Return
        isTimerBusy = True

        Try
            If lblClock IsNot Nothing AndAlso lblClock.IsHandleCreated Then
                lblClock.Text = DateTime.Now.ToString("HH:mm:ss")
            End If

            blinkCounter += 1
            If blinkCounter >= 10 Then
                blinkState = Not blinkState
                blinkCounter = 0
            End If

            ' ===== MODBUS READ & CONTROL =====
            isModbusOK = (modbusClient IsNot Nothing AndAlso modbusClient.Connected)

            If isModbusOK Then
                Try
                    inputs = Await Task.Run(Function() modbusClient.ReadDiscreteInputs(0, 16))

                    ' --- SAFETY INTERLOCKS (only when NOT already in E-Stop/Alarm) ---
                    If Not isEStopActive Then
                        If Not inputs(0) Then
                            TriggerEmergencyStop("HARDWARE E-STOP ACTIVATED (DI 00)")
                        End If
                        Dim robotMoving = (currentState = MachineStatus.B1_DISPENSE_START OrElse
                                           currentState = MachineStatus.B1_DISPENSE_WAIT)
                        If robotMoving AndAlso Not inputs(2) Then
                            TriggerEmergencyStop("CURTAIN SENSOR TRIPPED DURING OPERATION (DI 02)")
                        End If
                        If robotMoving AndAlso inputs(5) Then
                            TriggerEmergencyStop("ROBOT REPORTED FAULT (DI 05)")
                        End If
                    End If

                    ' --- STATE MACHINE ---
                        StepLogic()' Await removed for debug

                    ' --- APPLY BLINK COLORS ---
                    If currentBlinkColor = BlinkColor.YELLOW Then
                        outputs(1) = blinkState
                    ElseIf currentBlinkColor = BlinkColor.GREEN Then
                        outputs(2) = blinkState
                    End If

                    outputs(5) = isPaused
                    ApplyProgramBits()
                    Await Task.Run(Sub() modbusClient.WriteMultipleCoils(0, outputs))

                Catch ex As Exception
                    isModbusOK = False
                    Log("MODBUS FAULT: " & ex.Message)
                    Task.Run(Sub()
                        Try : modbusClient.Disconnect() : Catch : End Try
                        Try
                            modbusClient = New ModbusClient(config.ModbusIP, config.ModbusPort)
                            modbusClient.Connect()
                            Log("MODBUS AUTO-RECONNECT OK")
                        Catch rex As Exception
                            Log("MODBUS RECONNECT FAIL: " & rex.Message)
                        End Try
                    End Sub)
                End Try
            Else
                Task.Run(Sub()
                    Try
                        If modbusClient Is Nothing Then
                            modbusClient = New ModbusClient(config.ModbusIP, config.ModbusPort)
                        End If
                        If Not modbusClient.Connected Then modbusClient.Connect()
                        isModbusOK = modbusClient.Connected
                    Catch : End Try
                End Sub)
            End If

            RefreshDashboard()

        Catch ex As Exception
            Log("TIMER FAULT: " & ex.Message)
        Finally
            isTimerBusy = False
        End Try
    End Sub

    Private Sub ApplyProgramBits()
        Dim progNum = cmbProgramSelect.SelectedIndex + 1 ' 1-15
        ' Per PC I/O table: Program Select Bit 0-3 on DO 06, 07, 08, 09
        outputs(6)  = (progNum And &H1) <> 0  ' Bit 0 (LSB)
        outputs(7)  = (progNum And &H2) <> 0  ' Bit 1
        outputs(8)  = (progNum And &H4) <> 0  ' Bit 2
        outputs(9)  = (progNum And &H8) <> 0  ' Bit 3 (MSB)
        ' DO 10 = Cylinder Clamp - managed directly by state machine, never overwritten here
    End Sub
#End Region

    Private Sub StepLogic()
        Select Case currentState
            Case MachineStatus.IDLE
                ' START: Ready Green Light ON
                outputs(0) = False : outputs(1) = False : outputs(2) = True
                outputs(4) = False : outputs(5) = False : outputs(10) = False
                currentBlinkColor = BlinkColor.NONE
                ' Place the part on the jig -> Press Start Button (DI 01)
                If inputs(1) Then 
                    currentState = MachineStatus.A1_CLAMPING
                End If

            Case MachineStatus.A1_CLAMPING
                ' Clamp cylinder extend and clamp the part (DO 10)
                outputs(10) = True
                ' Wait for Cylinder Extend (Assume NC sensor DI 03 goes OFF + 500ms delay)
                If Not inputs(3) Then
                    Await Task.Delay(500)
                    ' Ready Yellow Light ON
                    outputs(1) = True
                    currentState = MachineStatus.A1_BARCODE_SCAN
                End If

            Case MachineStatus.A1_BARCODE_SCAN
                ' Barcode scanner active and record data
                Dim barcode As String = ""
                Try
                    Using client As New System.Net.Sockets.TcpClient()
                        Dim ct = client.ConnectAsync(config.KeyenceIP, config.KeyencePort)
                        If Await Task.WhenAny(ct, Task.Delay(2000)) IsNot ct Then Throw New Exception("Timeout")
                        Dim stream = client.GetStream()
                        Dim lonCmd = System.Text.Encoding.ASCII.GetBytes("LON" & vbCr)
                        Await stream.WriteAsync(lonCmd, 0, lonCmd.Length)
                        Await Task.Delay(100)
                        If stream.DataAvailable Then
                            Dim ack(63) As Byte : Await stream.ReadAsync(ack, 0, ack.Length)
                        End If
                        Dim buf(511) As Byte
                        Dim deadline = DateTime.Now.AddSeconds(4)
                        Do While DateTime.Now < deadline
                            If stream.DataAvailable Then
                                Dim n = Await stream.ReadAsync(buf, 0, buf.Length)
                                Dim raw2 = System.Text.Encoding.ASCII.GetString(buf, 0, n)
                                barcode = New String(raw2.Where(Function(cc) cc >= " "c AndAlso cc <= "~"c).ToArray()).Trim()
                                If Not String.IsNullOrEmpty(barcode) Then Exit Do
                            End If
                            Await Task.Delay(200)
                        Loop
                        Dim loffCmd = System.Text.Encoding.ASCII.GetBytes("LOFF" & vbCr)
                        Await stream.WriteAsync(loffCmd, 0, loffCmd.Length)
                    End Using
                Catch ex As Exception
                    Log("SCANNER READ ERROR")
                    isKeyenceOK = False
                End Try

                If Not String.IsNullOrEmpty(barcode) AndAlso barcode <> "ER" Then
                    isKeyenceOK = True
                    txtScannedModel.Invoke(Sub() txtScannedModel.Text = barcode)
                    ' Check Model
                    If barcode = config.MasterBarcode Then
                        currentState = MachineStatus.A1_MODEL_CONFIRM
                    Else
                        Log("MODEL MISMATCH: " & barcode)
                        currentState = MachineStatus.D1_ERROR_PATH
                    End If
                Else
                    ' Stay here to retry or error if too many fails? 
                    ' Flowchart doesn't specify scan retry limit, assuming infinite loop for now
                    Await Task.Delay(300)
                End If

            Case MachineStatus.A1_MODEL_CONFIRM
                ' YES Path: Press Start Button (DI 01) - secondary confirmation
                ' Visual hint: Yellow Blink
                currentBlinkColor = BlinkColor.YELLOW
                If inputs(1) Then
                    ' Check Curtain Sensor Active (NC = true means clear)
                    If inputs(2) Then
                        currentBlinkColor = BlinkColor.NONE
                        cycleStartTime = DateTime.Now
                        currentState = MachineStatus.B1_DISPENSE_START
                    End If
                End If

            Case MachineStatus.B1_DISPENSE_START
                ' Dispensing program start
                outputs(1) = True  ' Yellow Light On
                outputs(4) = True  ' Robot Start pulse
                Await Task.Delay(500)
                outputs(4) = False
                currentState = MachineStatus.B1_DISPENSE_WAIT

            Case MachineStatus.B1_DISPENSE_WAIT
                ' Dispense program finish (Robot Complete DI 04)
                If inputs(4) Then
                    currentState = MachineStatus.B1_DISPENSE_POST
                End If
                If inputs(5) Then currentState = MachineStatus.ALARM

            Case MachineStatus.B1_DISPENSE_POST
                ' Yellow light OFF
                outputs(1) = False
                ' Curtain sensor OFF (Normally machine pauses if tripped, but flowchart says "off" then "blink")
                ' Green light BLINK
                currentBlinkColor = BlinkColor.GREEN
                currentState = MachineStatus.B1_VISION_CHECK

            Case MachineStatus.B1_VISION_CHECK
                ' Vision system check
                Dim vResult = Await SendTcpRequest(config.CognexIP, config.CognexPort, "T" & vbCr)
                Dim isPass = vResult.Contains("1") OrElse vResult.ToUpper().Contains("OK") OrElse (inputs(6) AndAlso Not inputs(7))

                If isPass Then
                    currentBlinkColor = BlinkColor.NONE
                    txtVisionResult.Invoke(Sub() txtVisionResult.Text = "PASS")
                    currentState = MachineStatus.B1_FINISH_SUCCESS
                Else
                    currentBlinkColor = BlinkColor.NONE
                    txtVisionResult.Invoke(Sub() txtVisionResult.Text = "FAIL")
                    config.FailCount += 1
                    SaveSettings()
                    currentState = MachineStatus.D1_ERROR_PATH
                End If

            Case MachineStatus.B1_FINISH_SUCCESS
                ' Cylinder retract and unclamp (automatic)
                outputs(10) = False
                ' Cylinder retract sensor active (DI 03)
                If inputs(3) Then
                    ' Ready green light on
                    outputs(2) = True
                    ' Send data to server/Log
                    Log("CYCLE SUCCESS: " & txtScannedModel.Text)
                    cycleDuration = (DateTime.Now - cycleStartTime).TotalSeconds
                    config.PassCount += 1
                    SaveSettings()
                    currentState = MachineStatus.IDLE
                End If

            Case MachineStatus.D1_ERROR_PATH
                ' RED light ON
                outputs(0) = True
                outputs(1) = False
                outputs(2) = False
                ' Pop up window / Log state
                ' Press start button (DI 01) to acknowledge
                If inputs(1) Then
                    currentState = MachineStatus.A1_RETRACT_PATH
                End If

            Case MachineStatus.A1_RETRACT_PATH
                ' Cylinder retract
                outputs(10) = False
                ' Cylinder retract sensor active (DI 03)
                If inputs(3) Then
                    ' Red light OFF, Green light ON
                    outputs(0) = False
                    outputs(2) = True
                    ' Remove the part -> Return to C1 (IDLE)
                    ' Logic: wait for part to be removed? 
                    ' (If we detect DI 03 active, we are home. We just jump back)
                    currentState = MachineStatus.IDLE
                End If

            Case MachineStatus.ALARM
                outputs(0) = True ' Red On
                currentBlinkColor = BlinkColor.NONE
                ' Wait for manual reset from UI/HMI (handled in btnReset)
        End Select
    End Function
#End Region

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
        ' Guard: only trigger once until reset
        If isEStopActive OrElse isShowingEStopPopup Then Return
        
        isEStopActive = True
        isShowingEStopPopup = True
        currentBlinkColor = BlinkColor.NONE
        
        Array.Clear(outputs, 0, 11)
        outputs(0) = True  ' Red Light On
        outputs(3) = True  ' Robot E-Stop
        currentState = MachineStatus.ALARM
        Log("!!! E-STOP: " & reason)
        
        Me.Invoke(Sub()
            Me.TopMost = False
            MessageBox.Show(reason & vbCrLf & vbCrLf & "Press RESET to resume.",
                            "SYSTEM SAFETY FAULT", MessageBoxButtons.OK, MessageBoxIcon.Hand)
            Me.TopMost = True
            isShowingEStopPopup = False
        End Sub)
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
        ' MODBUS
        If picStatusModbus IsNot Nothing Then
            picStatusModbus.BackColor = If(isModbusOK, Color.Lime, Color.Red)
        End If
        
        ' COGNEX VISION - based on TCP reachability probe
        ' Refreshed async in background
        If picStatusVision IsNot Nothing Then
            picStatusVision.BackColor = If(isCognexOK, Color.Lime, Color.Red)
        End If
        
        ' KEYENCE SCANNER - based on last scan success
        If picStatusScanner IsNot Nothing Then
            picStatusScanner.BackColor = If(isKeyenceOK, Color.Lime, Color.Red)
        End If
    End Sub

    Private Async Sub ProbeDeviceHealthAsync()
        ' Probe Cognex In-Sight: check port 23 (Native/Telnet) OR port 80 (HTTP)
        ' Camera may only respond on one of these depending on firmware mode
        Dim cognex23 = PingPortAsync(config.CognexIP, 23, 500)
        Dim cognex80 = PingPortAsync(config.CognexIP, 80, 500)
        Await Task.WhenAll(cognex23, cognex80)
        isCognexOK = cognex23.Result OrElse cognex80.Result
        
        ' Probe Keyence: check config port (23 or 9004) also try 9004 as fallback
        Dim key1 = PingPortAsync(config.KeyenceIP, config.KeyencePort, 500)
        Dim key2 = PingPortAsync(config.KeyenceIP, 9004, 500)
        Await Task.WhenAll(key1, key2)
        isKeyenceOK = key1.Result OrElse key2.Result
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
        btnCfgScan = CreateIndustrialButton("SCAN LAN DEVICES", Color.DarkCyan, 250, 60)
        btnCfgSave = CreateIndustrialButton("SAVE & APPLY", Color.ForestGreen, 200, 60)
        btnCfgCancel = CreateIndustrialButton("CANCEL", Color.DimGray, 150, 60)
        
        AddHandler btnCfgScan.Click, AddressOf btnCfgScan_Click
        AddHandler btnCfgSave.Click, AddressOf btnCfgSave_Click
        AddHandler btnCfgCancel.Click, Sub() 
            pnlSettingsView.Visible = False
            pnlMainDashboard.Visible = True
        End Sub
        btnPnl.Controls.AddRange({btnCfgScan, btnCfgSave, btnCfgCancel})
        rightFlow.Controls.Add(btnPnl)
    End Sub

    Private Async Sub btnCfgScan_Click(sender As Object, e As EventArgs)
        btnCfgScan.Enabled = False
        btnCfgScan.Text = "SCANNING..."
        Log("LAN SCAN STARTED...")
        
        Try
            ' Get local IPv4 address
            Dim localIPStr As String = "192.168.1."
            Try
                Dim hostEntry = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName())
                For Each addr In hostEntry.AddressList
                    If addr.AddressFamily = Net.Sockets.AddressFamily.InterNetwork Then
                        Dim s = addr.ToString()
                        localIPStr = s.Substring(0, s.LastIndexOf(".") + 1)
                        Exit For
                    End If
                Next
            Catch : End Try
            
            Log("Scanning subnet: " & localIPStr & "1-254")
            
            Dim scanTasks As New System.Collections.Generic.List(Of Task(Of DeviceFound))()
            For i As Integer = 1 To 254
                Dim ip = localIPStr & i.ToString()
                scanTasks.Add(CheckDeviceAsync(ip))
            Next
            
            Dim results = Await Task.WhenAll(scanTasks)
            Dim foundDevices As New System.Collections.Generic.List(Of DeviceFound)()
            For Each r In results
                If r IsNot Nothing Then foundDevices.Add(r)
            Next
            
            If foundDevices.Count = 0 Then
                Me.TopMost = False
                MessageBox.Show("No hardware devices found on " & localIPStr & "1-254" & vbCrLf & "Make sure devices are powered and on the same network.", "SCAN COMPLETE", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Me.TopMost = True
            Else
                Dim msg = "Found " & foundDevices.Count & " device(s):" & vbCrLf & vbCrLf
                Dim modbusFound As String = Nothing
                Dim cognexFound As String = Nothing
                Dim keyenceFound As String = Nothing
                
                For Each d In foundDevices
                    msg &= "  IP: " & d.IP & "  ->  " & d.DeviceType & vbCrLf
                    If d.DeviceType.Contains("MODBUS") Then modbusFound = d.IP
                    If d.DeviceType.Contains("COGNEX") Then cognexFound = d.IP
                    If d.DeviceType.Contains("KEYENCE") Then keyenceFound = d.IP
                Next
                
                ' Auto-fill and auto-connect
                If modbusFound IsNot Nothing Then
                    txtCfgModbusIP.Text = modbusFound
                    config.ModbusIP = modbusFound
                    ConnectModbus()
                    msg &= vbCrLf & "[OK] Modbus connected to " & modbusFound
                End If
                If cognexFound IsNot Nothing Then
                    txtCfgVisionIP.Text = cognexFound
                    config.CognexIP = cognexFound
                    msg &= vbCrLf & "[OK] Cognex Vision found at " & cognexFound
                End If
                If keyenceFound IsNot Nothing Then
                    txtCfgScannerIP.Text = keyenceFound
                    config.KeyenceIP = keyenceFound
                    msg &= vbCrLf & "[OK] Keyence Scanner found at " & keyenceFound
                End If
                
                SaveSettings()
                
                Me.TopMost = False
                MessageBox.Show(msg & vbCrLf & vbCrLf & "Settings auto-saved. Review and click SAVE & APPLY if needed.", "SCAN & CONNECT SUCCESS", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Me.TopMost = True
            End If
            
        Catch ex As Exception
            Me.TopMost = False
            MessageBox.Show("Scan Error: " & ex.Message, "SCAN ERROR", MessageBoxButtons.OK, MessageBoxIcon.Error)
            Me.TopMost = True
        Finally
            btnCfgScan.Enabled = True
            btnCfgScan.Text = "SCAN LAN DEVICES"
            Log("LAN SCAN COMPLETE.")
        End Try
    End Sub

    Private Async Function CheckDeviceAsync(ip As String) As Task(Of DeviceFound)
        ' Step 1: ICMP Ping first (fast, works even if TCP ports are closed)
        Dim isAlive = Await Task.Run(Function()
            Try
                Dim pinger As New Net.NetworkInformation.Ping()
                Dim reply = pinger.Send(ip, 200)  ' 200ms timeout
                Return (reply.Status = Net.NetworkInformation.IPStatus.Success)
            Catch
                Return False
            End Try
        End Function)
        
        If Not isAlive Then Return Nothing
        
        ' Step 2: Check known ports on live host
        ' Modbus TCP I/O -> port 502
        If Await PingPortAsync(ip, 502, 300) Then Return New DeviceFound(ip, "MODBUS TCP I/O", 502)
        
        ' Cognex In-Sight -> port 80 (HTTP/EasyBuilder) or 8080 or 23 (Telnet Native)
        If Await PingPortAsync(ip, 80, 300) Then Return New DeviceFound(ip, "COGNEX VISION (Port 80)", 80)
        If Await PingPortAsync(ip, 8080, 300) Then Return New DeviceFound(ip, "COGNEX VISION (Port 8080)", 8080)
        
        ' Keyence Barcode -> port 9004 (BL/SR series) or 23 (Telnet)
        If Await PingPortAsync(ip, 9004, 300) Then Return New DeviceFound(ip, "KEYENCE SCANNER (Port 9004)", 9004)
        
        ' Port 23 - could be Cognex or Keyence in Telnet mode
        If Await PingPortAsync(ip, 23, 300) Then Return New DeviceFound(ip, "KEYENCE/COGNEX Telnet (Port 23)", 23)
        
        ' Active but unknown type
        Return New DeviceFound(ip, "Active Device (Unknown)", 0)
    End Function

    Private Async Function PingPortAsync(ip As String, port As Integer, timeoutMs As Integer) As Task(Of Boolean)
        Try
            Using client As New Net.Sockets.TcpClient()
                client.ReceiveTimeout = timeoutMs
                client.SendTimeout = timeoutMs
                Dim connectTask = client.ConnectAsync(ip, port)
                If Await Task.WhenAny(connectTask, Task.Delay(timeoutMs)) Is connectTask AndAlso client.Connected Then
                    Return True
                End If
            End Using
        Catch : End Try
        Return False
    End Function

    Private Sub btnEStop_Click(sender As Object, e As EventArgs)
        ForceExit()
    End Sub

    ''' <summary>Safe exit: try gracefully, fall back to hard kill</summary>
    Private Sub ForceExit()
        Try
            Me.TopMost = False
            Dim ans = MessageBox.Show("CLOSE THE SYSTEM?" & vbCrLf & "(Hold Ctrl+Q to force-quit any time)",
                                      "EXIT CONFIRM", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            If ans = DialogResult.Yes Then
                HardExit()
            Else
                Me.TopMost = True
            End If
        Catch
            HardExit()
        End Try
    End Sub

    Private Sub HardExit()
        Try : tmrMainLoop?.Stop() : Catch : End Try
        Try : tmrCamera?.Stop() : Catch : End Try
        Try : modbusClient?.Disconnect() : Catch : End Try
        Environment.Exit(0)
    End Sub

    Private Sub Form1_KeyDown(sender As Object, e As KeyEventArgs)
        Select Case e.KeyCode
            Case Keys.Escape
                ForceExit()
            Case Keys.Q
                If e.Control Then HardExit()  ' Ctrl+Q = immediate hard exit
        End Select
    End Sub

    Protected Overrides Sub OnFormClosing(e As System.Windows.Forms.FormClosingEventArgs)
        HardExit()
        MyBase.OnFormClosing(e)
    End Sub

    Private Sub ConnectModbus()
        Task.Run(Sub()
            Try
                If modbusClient IsNot Nothing AndAlso modbusClient.Connected Then
                    modbusClient.Disconnect()
                End If
                modbusClient = New ModbusClient(config.ModbusIP, config.ModbusPort)
                modbusClient.Connect()
                Log("Modbus reconnected to: " & config.ModbusIP)
            Catch ex As Exception
                Log("Modbus reconnect failed: " & ex.Message)
            End Try
        End Sub)
    End Sub
#End Region




End Class


