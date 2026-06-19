using Godot;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Runtime.InteropServices;

#nullable enable

public partial class V2EvdevServer : Control
{
	// ── evdev constants (from: adb shell getevent -p /dev/input/event8) ──
	private const string AdbDevice   = "/dev/input/event8";
	private const float  AbsXMax     = 14679f; // ABS_X short axis
	private const float  AbsYMax     = 23487f; // ABS_Y long axis

	// ── protocol ──────────────────────────────────────────────────────────
	private const int TcpPort = 42425;

	// ── mapping received from Android ─────────────────────────────────────
	private volatile float  _areaX       = 0f;
	private volatile float  _areaY       = 0f;
	private volatile float  _areaW       = 1f;
	private volatile float  _areaH       = 1f;
	private volatile bool   _tapToClick  = true;
	private volatile bool   _mappingLive = false; // true only while TCP alive
	private volatile float  _currentFx   = 0f;
	private volatile float  _currentFy   = 0f;

	// ── runtime ───────────────────────────────────────────────────────────
	private bool    _running = false;
	private Process? _adbProc;
	private Thread?  _adbThread;
	private Thread?  _tcpThread;
	private bool    _leftDown = false;
	private bool    _rightDown = false;

	// ── Touch state machine ────────────────────────────────────────────────
	private enum TouchState { Idle, Touching, Moving, Dragging }
	private TouchState _touchState = TouchState.Idle;
	private long _touchDownTimeMs = 0;
	private float _touchDownX = 0;
	private float _touchDownY = 0;
	private const float MoveThreshold = 100f; // raw evdev units (~1.5mm)
	private const long DragDelayMs = 200; // milliseconds
	private readonly object _touchLock = new object();

	// ── Hz counter ────────────────────────────────────────────────────────
	private int    _hzCounter = 0;
	private int    _currentHz = 0;
	private double _hzTimer   = 0.0;

	// ── UI nodes ──────────────────────────────────────────────────────────
	private Label?  _statusLabel;
	private Label?  _hzLabel;
	private Button? _exitButton;
	private bool    _allowQuit = false;

	// ── Win32 ─────────────────────────────────────────────────────────────
	[DllImport("user32.dll", EntryPoint = "mouse_event")]
	private static extern void MouseEvent(uint dwFlags, uint dx, uint dy, uint data, UIntPtr extra);

	private const uint MouseMove     = 0x0001;
	private const uint MouseDown     = 0x0002;
	private const uint MouseUp       = 0x0004;
	private const uint MouseRightDown= 0x0008;
	private const uint MouseRightUp  = 0x0010;
	private const uint MouseAbsolute = 0x8000;

	// ─────────────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_statusLabel = GetNodeOrNull<Label>("Panel/VBox/Status");
		_hzLabel     = GetNodeOrNull<Label>("Panel/VBox/HzLabel");
		_exitButton  = GetNodeOrNull<Button>("Panel/VBox/ExitButton");

		if (_exitButton != null)
			_exitButton.Pressed += () => { _allowQuit = true; GetTree().Quit(); };

		GetTree().AutoAcceptQuit = false;

		_running = true;

		// Start TCP listener for mapping from Android
		_tcpThread = new Thread(TcpLoop) { IsBackground = true };
		_tcpThread.Start();

		// Auto-start ADB removed; now starts on TCP connect
		// StartAdb();

		SetStatus("Waiting for Android client on TCP :" + TcpPort);
	}

	public override void _Process(double delta)
	{
		_hzTimer += delta;
		if (_hzTimer >= 1.0)
		{
			_currentHz = _hzCounter;
			_hzCounter = 0;
			_hzTimer   = 0.0;
			if (_hzLabel != null)
				_hzLabel.Text = _mappingLive
					? $"{_currentHz} Hz"
					: "-- Hz (waiting for client)";
		}

		if (_mappingLive && _statusLabel != null)
		{
			TouchState curState;
			lock (_touchLock) { curState = _touchState; }
			_statusLabel.Text = $"Live ({curState}) | Pos: ({_currentFx:F2}, {_currentFy:F2})\nArea: ({_areaX:F2},{_areaY:F2}) {_areaW:F2}×{_areaH:F2} | Tap: {_tapToClick}";
		}

		// Check for drag timeout (exactly at 200ms)
		lock (_touchLock)
		{
			if (_touchState == TouchState.Touching)
			{
				long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _touchDownTimeMs;
				if (elapsed >= DragDelayMs)
				{
					_touchState = TouchState.Dragging;
					MouseEvent(MouseDown, 0, 0, 0, UIntPtr.Zero);
					_leftDown = true;
				}
			}
		}
	}

	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest && !_allowQuit)
			return; // Block accidental close
	}

	public override void _ExitTree()
	{
		_running = false;
		KillAdb();
		if (_leftDown)
		{
			MouseEvent(MouseUp, 0, 0, 0, UIntPtr.Zero);
			_leftDown = false;
		}
		lock (_touchLock) { _touchState = TouchState.Idle; }
		if (_rightDown)
		{
			MouseEvent(MouseRightUp, 0, 0, 0, UIntPtr.Zero);
			_rightDown = false;
		}
	}

	private void KillAdb()
	{
		if (_adbProc == null) return;
		try
		{
			// taskkill /F /T kills cmd.exe AND its child adb.exe
			var kill = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName        = "taskkill",
					Arguments       = $"/F /T /PID {_adbProc.Id}",
					CreateNoWindow  = true,
					UseShellExecute = false
				}
			};
			kill.Start();
			kill.WaitForExit(1000);
		}
		catch { }
		try { _adbProc.Kill(); } catch { }
		_adbProc.Dispose();
		_adbProc = null;
	}

	// ── ADB ───────────────────────────────────────────────────────────────

	private void StartAdb()
	{
		// Kill any previous process
		try { _adbProc?.Kill(); } catch { }
		_adbProc?.Dispose();

		try
		{
			string adbCommand = "adb";
			if (System.IO.File.Exists("adb\\adb.exe"))
			{
				adbCommand = "\"adb\\adb.exe\"";
			}

			// Use cmd.exe /c so it inherits the user's PATH (where adb.exe lives)
			_adbProc = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName               = "cmd.exe",
					Arguments              = $"/c {adbCommand} shell getevent {AdbDevice}",
					UseShellExecute        = false,
					RedirectStandardOutput = true,
					RedirectStandardError  = true,
					CreateNoWindow         = true,
					StandardOutputEncoding = System.Text.Encoding.ASCII
				}
			};
			_adbProc.Start();

			_adbThread = new Thread(AdbLoop) { IsBackground = true };
			_adbThread.Start();

			GD.Print($"ADB started: cmd.exe /c {adbCommand} shell getevent {AdbDevice}");
			CallDeferred(nameof(SetStatus), $"ADB running ({AdbDevice})\nWaiting for Android client on TCP :{TcpPort}");
		}
		catch (Exception ex)
		{
			GD.PrintErr("ADB start failed: " + ex.Message);
			CallDeferred(nameof(SetStatus), "ADB failed: " + ex.Message + "\nIs adb missing?");
		}
	}

	private void AdbLoop()
	{
		if (_adbProc == null) return;
		System.IO.StreamReader reader;
		try { reader = _adbProc.StandardOutput; } catch { return; }

		float curRawX = 0f, curRawY = 0f;
		bool  curDown = false;
		bool  curRightDown = false;

		while (_running)
		{
			string? line = null;
			try { line = reader.ReadLine(); } catch { break; }
			if (line == null) break;

			var trimmed = line.Trim();
			if (trimmed.Length == 0) continue;

			// Strip device prefix if present: "/dev/input/event8: 0003 0000 00002672"
			var colonIdx = trimmed.IndexOf(':');
			if (colonIdx >= 0 && colonIdx < 30)
				trimmed = trimmed[(colonIdx + 1)..].TrimStart();

			// Format: "0003 0000 00002672"
			var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 3) continue;

			try
			{
				uint type  = Convert.ToUInt32(parts[0], 16);
				uint code  = Convert.ToUInt32(parts[1], 16);
				uint uval  = Convert.ToUInt32(parts[2], 16);
				int  value = unchecked((int)uval); // keep sign for tilt etc.

				switch (type)
				{
					case 0x0003: // EV_ABS
						if (code == 0x0000) curRawX = value; // ABS_X (short axis)
						if (code == 0x0001) curRawY = value; // ABS_Y (long axis)
						break;

					case 0x0001: // EV_KEY
						// 0x0140 is BTN_TOOL_PEN (hover), 0x014a is BTN_TOUCH (surface touch)
						// 0x014b is BTN_STYLUS (S-Pen side button)
						if (code == 0x014a) curDown = (value == 1); 
						else if (code == 0x014b) curRightDown = (value == 1);
						break;

					case 0x0000: // EV_SYN
						if (code == 0x0000 && _mappingLive) // SYN_REPORT
						{
							ApplyMove(curRawX, curRawY, curDown, curRightDown);
							_hzCounter++;
						}
						break;
				}
			}
			catch { /* malformed line – skip */ }
		}

		GD.Print("ADB loop ended.");
	}

	private void ApplyMove(float rawX, float rawY, bool down, bool rightDown)
	{
		// Tablet in landscape:
		//   evdev ABS_Y (long axis 0-23487) → screen X, but direction is reversed
		//   evdev ABS_X (short axis 0-14679) → screen Y
		float sNormX = 1f - (rawY / AbsYMax); // flip X to correct direction
		float sNormY = rawX / AbsXMax;

		// Map through the user-defined touch area
		float fx = (sNormX - _areaX) / _areaW;
		float fy = (sNormY - _areaY) / _areaH;
		fx = Math.Clamp(fx, 0f, 1f);
		fy = Math.Clamp(fy, 0f, 1f);

		_currentFx = fx;
		_currentFy = fy;

		// mouse_event with MOUSEEVENTF_ABSOLUTE maps 0.0-1.0 to 0-65535
		uint ax = (uint)(fx * 65535f);
		uint ay = (uint)(fy * 65535f);
		
		MouseEvent(MouseAbsolute | MouseMove, ax, ay, 0, UIntPtr.Zero);

		if (_tapToClick)
		{
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			lock (_touchLock)
			{
				if (down)
				{
					if (_touchState == TouchState.Idle)
					{
						_touchState = TouchState.Touching;
						_touchDownTimeMs = nowMs;
						_touchDownX = rawX;
						_touchDownY = rawY;
					}
					else if (_touchState == TouchState.Touching)
					{
						float dx = rawX - _touchDownX;
						float dy = rawY - _touchDownY;
						float distSq = dx * dx + dy * dy;

						if (distSq > MoveThreshold * MoveThreshold)
						{
							// Moved past threshold BEFORE 200ms elapsed -> lock to Moving mode
							_touchState = TouchState.Moving;
						}
					}
				}
				else // pen lifted
				{
					if (_touchState == TouchState.Touching)
					{
						// Tap! Lifted before 200ms without significant movement.
						MouseEvent(MouseDown, 0, 0, 0, UIntPtr.Zero);
						MouseEvent(MouseUp, 0, 0, 0, UIntPtr.Zero);
						_leftDown = false;
					}
					else if (_touchState == TouchState.Dragging)
					{
						// Finish drag
						MouseEvent(MouseUp, 0, 0, 0, UIntPtr.Zero);
						_leftDown = false;
					}
					_touchState = TouchState.Idle;
				}

				if (rightDown && !_rightDown)
				{
					MouseEvent(MouseRightDown, 0, 0, 0, UIntPtr.Zero);
					_rightDown = true;
				}
				else if (!rightDown && _rightDown)
				{
					MouseEvent(MouseRightUp, 0, 0, 0, UIntPtr.Zero);
					_rightDown = false;
				}
			}
		}
	}

	// ── TCP (receives mapping from Android Godot app) ─────────────────────

	private void TcpLoop()
	{
		var listener = new TcpListener(IPAddress.Any, TcpPort);
		listener.Start();
		GD.Print($"TCP mapping listener on :{TcpPort}");

		while (_running)
		{
			TcpClient? client = null;
			try
			{
				client = listener.AcceptTcpClient();
				GD.Print("Android client connected.");
				CallDeferred(nameof(SetStatus), "Android connected – receiving mapping...");

				// Start ADB only when client connects
				StartAdb();

				var stream = client.GetStream();
				var buf    = new byte[17];

				// Read mapping packets until client disconnects
				while (_running && client.Connected)
				{
					int got = 0;
					while (got < 17)
					{
						int n = stream.Read(buf, got, 17 - got);
						if (n == 0) goto disconnected; // clean disconnect
						got += n;
					}
					// Parse mapping
					_areaX      = BitConverter.ToSingle(buf, 0);
					_areaY      = BitConverter.ToSingle(buf, 4);
					_areaW      = Math.Max(BitConverter.ToSingle(buf, 8),  0.01f);
					_areaH      = Math.Max(BitConverter.ToSingle(buf, 12), 0.01f);
					_tapToClick = buf[16] != 0;

					// Release mouse button if held when mapping changes
					// (prevents "stuck left click" after resizing the touch area)
					if (_leftDown)
					{
						MouseEvent(MouseUp, 0, 0, 0, UIntPtr.Zero);
						_leftDown = false;
					}
					lock (_touchLock) { _touchState = TouchState.Idle; }
					if (_rightDown)
					{
						MouseEvent(MouseRightUp, 0, 0, 0, UIntPtr.Zero);
						_rightDown = false;
					}

					_mappingLive = true;

					GD.Print($"Mapping: area=({_areaX:F3},{_areaY:F3}) size=({_areaW:F3},{_areaH:F3}) tap={_tapToClick}");
					// UI update is now handled dynamically in _Process
				}
			}
			catch (Exception ex) when (_running)
			{
				GD.PrintErr("TCP error: " + ex.Message);
			}

			disconnected:
			_mappingLive = false;
			// Release mouse button on disconnect
			if (_leftDown)
			{
				MouseEvent(MouseUp, 0, 0, 0, UIntPtr.Zero);
				_leftDown = false;
			}
			lock (_touchLock) { _touchState = TouchState.Idle; }
			if (_rightDown)
			{
				MouseEvent(MouseRightUp, 0, 0, 0, UIntPtr.Zero);
				_rightDown = false;
			}
			client?.Close();

			// Kill ADB when client disconnects
			KillAdb();

			CallDeferred(nameof(SetStatus), "Client disconnected. Waiting on TCP :" + TcpPort);
			GD.Print("Android client disconnected.");
		}

		listener.Stop();
	}

	private void SetStatus(string msg)
	{
		if (_statusLabel != null)
			_statusLabel.Text = msg;
	}
}
