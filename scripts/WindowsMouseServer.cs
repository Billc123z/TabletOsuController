using Godot;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

public partial class WindowsMouseServer : Control
{
	private const int StateUp = 0;
	private const int StateDown = 1;
	private const int PacketSize = 9;
	private const uint MouseEventLeftDown = 0x0002;
	private const uint MouseEventLeftUp = 0x0004;

	[Export(PropertyHint.Range, "1024,65535,1")]
	public int ListenPort { get; set; } = 42424;

	[Export]
	public bool EnableMouseControl { get; set; } = true;

	[Export]
	public bool LogPackets { get; set; } = false;

	private UdpClient? _udp;
	private CancellationTokenSource? _cancelSource;
	private Task? _receiveTask;
	private bool _leftButtonDown;
	private bool _allowQuit;
	private string _logPath = "";
	private double _heartbeatSeconds;
	private Label? _statusLabel;
	private Button? _exitButton;

	public override void _Ready()
	{
		_logPath = ProjectSettings.GlobalizePath("user://server.log");
		_statusLabel = GetNodeOrNull<Label>("RootMargin/Panel/VBox/Status");
		_exitButton = GetNodeOrNull<Button>("RootMargin/Panel/VBox/ExitButton");
		if (_exitButton is not null)
		{
			_exitButton.Pressed += RequestQuit;
		}

		GetTree().AutoAcceptQuit = false;
		SetProcess(true);
		Log("Server _Ready started.");

		try
		{
			if (!OperatingSystem.IsWindows())
			{
				Log("This server can only control the mouse on Windows.");
				SetStatus("Server can only control the mouse on Windows.");
				return;
			}

			_udp = new UdpClient(new IPEndPoint(IPAddress.Any, ListenPort));
			_udp.Client.ReceiveTimeout = 100;
			_cancelSource = new CancellationTokenSource();
			_receiveTask = Task.Run(() => ReceiveLoop(_cancelSource.Token));

			Log($"Windows mouse UDP server listening on 0.0.0.0:{ListenPort}");
			SetStatus($"Listening on UDP {ListenPort}. Mouse control: {EnableMouseControl}");
		}
		catch (Exception ex)
		{
			LogException("Failed to start server.", ex);
			SetStatus($"Failed to start. See log: {_logPath}");
			GD.PushError($"Failed to start UDP server. See log: {_logPath}");
		}
	}

	public override void _Process(double delta)
	{
		_heartbeatSeconds += delta;
		if (_heartbeatSeconds >= 5.0)
		{
			_heartbeatSeconds = 0.0;
			Log("Heartbeat: server scene is still alive.");
		}
	}

	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest)
		{
			if (_allowQuit)
			{
				return;
			}

			Log("Window close request received. Server stays open because AutoAcceptQuit is false. Use Exit Server to quit.");
		}
		else if (what == NotificationWMGoBackRequest)
		{
			Log("Go-back request received. Server stays open because AutoAcceptQuit is false.");
		}
	}

	public override void _ExitTree()
	{
		Log("Server _ExitTree started.");

		try
		{
			_cancelSource?.Cancel();
			_udp?.Close();

			if (_leftButtonDown)
			{
				MouseEvent(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
				_leftButtonDown = false;
			}
		}
		catch (Exception ex)
		{
			LogException("Error while shutting down server.", ex);
		}
		finally
		{
			_udp?.Dispose();
			_cancelSource?.Dispose();
		}
	}

	private void ReceiveLoop(CancellationToken cancellationToken)
	{
		Log("Receive loop started.");

		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				if (_udp is null)
				{
					return;
				}

				var remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
				var packet = _udp.Receive(ref remoteEndPoint);
				if (TryParsePacket(packet, out var state, out var normX, out var normY))
				{
					ApplyPointerState(state, normX, normY);
				}
			}
			catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
			{
			}
			catch (SocketException ex) when (cancellationToken.IsCancellationRequested || ex.SocketErrorCode == SocketError.Interrupted || ex.SocketErrorCode == SocketError.OperationAborted)
			{
				return;
			}
			catch (ObjectDisposedException)
			{
				return;
			}
			catch (Exception ex)
			{
				LogException("Receive loop error.", ex);
				Thread.Sleep(100);
			}
		}

		Log("Receive loop stopped.");
	}

	private static bool TryParsePacket(byte[] packet, out int state, out float normX, out float normY)
	{
		state = StateUp;
		normX = 0.0f;
		normY = 0.0f;

		if (packet.Length != PacketSize)
		{
			return false;
		}

		state = packet[0];
		normX = BitConverter.ToSingle(packet, 1);
		normY = BitConverter.ToSingle(packet, 5);

		if ((state != StateUp && state != StateDown) || float.IsNaN(normX) || float.IsNaN(normY))
		{
			return false;
		}

		normX = Math.Clamp(normX, 0.0f, 1.0f);
		normY = Math.Clamp(normY, 0.0f, 1.0f);
		return true;
	}

	private void ApplyPointerState(int state, float normX, float normY)
	{
		try
		{
			var screenWidth = GetSystemMetrics(SystemMetricScreenWidth);
			var screenHeight = GetSystemMetrics(SystemMetricScreenHeight);
			if (screenWidth <= 0 || screenHeight <= 0)
			{
				Log($"Invalid screen size: {screenWidth}x{screenHeight}");
				return;
			}

			var pixelX = Math.Clamp((int)MathF.Round(normX * (screenWidth - 1)), 0, screenWidth - 1);
			var pixelY = Math.Clamp((int)MathF.Round(normY * (screenHeight - 1)), 0, screenHeight - 1);

			if (LogPackets)
			{
				Log($"state={state} norm=({normX:0.0000},{normY:0.0000}) pixel=({pixelX},{pixelY})");
			}

			if (!EnableMouseControl)
			{
				return;
			}

			SetCursorPos(pixelX, pixelY);

			if (state == StateDown && !_leftButtonDown)
			{
				MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
				_leftButtonDown = true;
			}
			else if (state == StateUp && _leftButtonDown)
			{
				MouseEvent(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
				_leftButtonDown = false;
			}
		}
		catch (Exception ex)
		{
			LogException("Failed to apply pointer state.", ex);
		}
	}

	private void Log(string message)
	{
		var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
		GD.Print(line);

		if (string.IsNullOrEmpty(_logPath))
		{
			return;
		}

		try
		{
			var logDirectory = Path.GetDirectoryName(_logPath);
			if (!string.IsNullOrEmpty(logDirectory))
			{
				Directory.CreateDirectory(logDirectory);
			}

			File.AppendAllText(_logPath, line + System.Environment.NewLine);
		}
		catch
		{
		}
	}

	private void LogException(string message, Exception ex)
	{
		Log($"{message} {ex.GetType().Name}: {ex.Message}{System.Environment.NewLine}{ex.StackTrace}");
	}

	private void SetStatus(string message)
	{
		if (_statusLabel is not null)
		{
			_statusLabel.Text = message;
		}
	}

	private void RequestQuit()
	{
		Log("Exit Server button pressed.");
		_allowQuit = true;
		GetTree().Quit();
	}

	private const int SystemMetricScreenWidth = 0;
	private const int SystemMetricScreenHeight = 1;

	[DllImport("user32.dll")]
	private static extern bool SetCursorPos(int x, int y);

	[DllImport("user32.dll", EntryPoint = "mouse_event")]
	private static extern void MouseEvent(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int nIndex);
}
