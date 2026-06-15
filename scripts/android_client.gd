extends Control

const STATE_UP := 0
const STATE_DOWN := 1
const PACKET_SIZE := 9
const MIN_AREA_SIZE := Vector2(160.0, 120.0)
const HANDLE_SIZE := Vector2(140.0, 70.0)
const RESIZE_HANDLE_SIZE := Vector2(72.0, 60.0)

@export var server_ip := "10.48.90.189"
@export_range(1024, 65535, 1) var server_port := 42424

@onready var settings_layer: MarginContainer = $SettingsLayer
@onready var touch_layer: Control = $TouchLayer
@onready var status_label: Label = $SettingsLayer/Panel/SettingsVBox/Status
@onready var server_ip_edit: LineEdit = $SettingsLayer/Panel/SettingsVBox/ServerRow/ServerIp
@onready var port_spin: SpinBox = $SettingsLayer/Panel/SettingsVBox/PortRow/Port
@onready var apply_button: Button = $SettingsLayer/Panel/SettingsVBox/ApplyButton
@onready var touch_area: ColorRect = $TouchLayer/TouchArea
@onready var center_handle: Button = $TouchLayer/CenterHandle
@onready var resize_handle: Button = $TouchLayer/ResizeHandle
@onready var edit_toggle: Button = $TouchLayer/Toolbar/EditToggle
@onready var back_button: Button = $TouchLayer/Toolbar/BackButton
@onready var hz_label: Label = $TouchLayer/Toolbar/HzLabel
@onready var tap_to_click_checkbox: CheckButton = $SettingsLayer/Panel/SettingsVBox/TapToClick

var _udp := PacketPeerUDP.new()
var _is_touching := false
var _active_touch_index := -1
var _packets_sent := 0
var _enable_pen_clicks := true

# Hz monitor
var _hz_counter := 0
var _hz_timer := 0.0
var _current_hz := 0

var _edit_mode := true
var _edit_action := ""
var _edit_touch_index := -1
var _edit_start_touch := Vector2.ZERO
var _edit_start_area_position := Vector2.ZERO
var _edit_start_area_size := Vector2.ZERO

const LAYOUT_PATH := "user://layout.cfg"


func _ready() -> void:
	Input.use_accumulated_input = false
	server_ip_edit.text = server_ip
	port_spin.value = server_port
	apply_button.pressed.connect(_apply_udp_target)
	back_button.pressed.connect(_show_settings)
	edit_toggle.toggled.connect(_set_edit_mode)
	
	tap_to_click_checkbox.button_pressed = _enable_pen_clicks
	tap_to_click_checkbox.toggled.connect(func(t): _enable_pen_clicks = t)
	
	_load_layout()
	_show_settings()
	_set_edit_mode(true)
	call_deferred("_clamp_touch_area_to_viewport")


func _process(delta: float) -> void:
	_hz_timer += delta
	if _hz_timer >= 1.0:
		_current_hz = _hz_counter
		_hz_counter = 0
		_hz_timer = 0.0
		hz_label.text = "%d Hz" % _current_hz


func _notification(what: int) -> void:
	if what == NOTIFICATION_RESIZED:
		_clamp_touch_area_to_viewport()


func _get_event_index(event: InputEvent) -> int:
	if event is InputEventScreenTouch or event is InputEventScreenDrag:
		return event.index
	elif event is InputEventMouseButton or event is InputEventMouseMotion:
		return 99 # Use 99 to distinguish mouse/pen from fingers
	return -1


func _input(event: InputEvent) -> void:
	if not touch_layer.visible:
		return

	# S-Pen / Mouse hover (no button held) → send movement with STATE_UP
	if event is InputEventMouseMotion:
		var pos = event.position
		var is_pressed = (event.button_mask & MOUSE_BUTTON_MASK_LEFT) != 0
		if _edit_mode:
			if is_pressed:
				_handle_edit_drag(99, pos)
		else:
			# Always send for hover (is_pressed=false) and drag (is_pressed=true)
			_handle_drag(99, is_pressed, pos)
		return

	if event is InputEventMouseButton:
		# Only react to LEFT button; ignore release-only events that aren't real clicks
		if event.button_index != MOUSE_BUTTON_LEFT:
			return
		var is_pressed = event.pressed
		var pos = event.position
		if _edit_mode:
			_handle_edit_touch(99, is_pressed, pos)
		else:
			_handle_touch(99, is_pressed, pos)
		return

	if event is InputEventScreenTouch:
		var idx = event.index
		var is_pressed = event.pressed
		var pos = event.position
		if _edit_mode:
			_handle_edit_touch(idx, is_pressed, pos)
		else:
			_handle_touch(idx, is_pressed, pos)
		return

	if event is InputEventScreenDrag:
		var idx = event.index
		var pos = event.position
		if _edit_mode:
			_handle_edit_drag(idx, pos)
		else:
			_handle_drag(idx, true, pos)


func _handle_touch(idx: int, is_pressed: bool, pos: Vector2) -> void:
	if is_pressed:
		if _is_over_toolbar(pos):
			return
		if not _get_touch_area_rect().has_point(pos):
			return

		_is_touching = true
		_active_touch_index = idx
		
		var state_to_send = STATE_UP
		if _enable_pen_clicks:
			state_to_send = STATE_DOWN
		_send_touch_packet(state_to_send, pos)
	else:
		if idx == _active_touch_index:
			_send_touch_packet(STATE_UP, pos)
			_is_touching = false
			_active_touch_index = -1


func _handle_drag(idx: int, is_pressed: bool, pos: Vector2) -> void:
	if _active_touch_index == -1 and not is_pressed:
		if _is_over_toolbar(pos):
			return
		if not _get_touch_area_rect().has_point(pos):
			return

	if _active_touch_index == -1 or idx == _active_touch_index:
		_active_touch_index = idx
		_is_touching = is_pressed
		
		var state_to_send = STATE_UP
		if is_pressed and _enable_pen_clicks:
			state_to_send = STATE_DOWN
			
		_send_touch_packet(state_to_send, pos)


func _handle_edit_touch(idx: int, is_pressed: bool, pos: Vector2) -> void:
	if is_pressed:
		if _get_control_rect(resize_handle).has_point(pos):
			_begin_area_edit("resize", idx, pos)
		elif _get_control_rect(center_handle).has_point(pos):
			_begin_area_edit("move", idx, pos)
	else:
		if idx == _edit_touch_index:
			_edit_action = ""
			_edit_touch_index = -1


func _handle_edit_drag(idx: int, pos: Vector2) -> void:
	if idx != _edit_touch_index or _edit_action.is_empty():
		return

	var delta := pos - _edit_start_touch
	if _edit_action == "move":
		touch_area.global_position = _edit_start_area_position + delta
	elif _edit_action == "resize":
		touch_area.size = Vector2(
			maxf(_edit_start_area_size.x + delta.x, MIN_AREA_SIZE.x),
			maxf(_edit_start_area_size.y + delta.y, MIN_AREA_SIZE.y)
		)

	_clamp_touch_area_to_viewport()


func _begin_area_edit(action: String, touch_index: int, touch_position: Vector2) -> void:
	_edit_action = action
	_edit_touch_index = touch_index
	_edit_start_touch = touch_position
	_edit_start_area_position = touch_area.global_position
	_edit_start_area_size = touch_area.size


func _send_touch_packet(state: int, position: Vector2) -> void:
	var normalized := _normalize_position(position)
	var packet := PackedByteArray()
	packet.resize(PACKET_SIZE)
	packet.encode_u8(0, state)
	packet.encode_float(1, normalized.x)
	packet.encode_float(5, normalized.y)
	_udp.put_packet(packet)
	_packets_sent += 1
	_hz_counter += 1


func _normalize_position(position: Vector2) -> Vector2:
	var area_position := touch_area.global_position
	var area_size := touch_area.size
	if area_size.x <= 0.0 or area_size.y <= 0.0:
		return Vector2.ZERO

	return Vector2(
		clamp((position.x - area_position.x) / area_size.x, 0.0, 1.0),
		clamp((position.y - area_position.y) / area_size.y, 0.0, 1.0)
	)


func _apply_udp_target() -> void:
	server_ip = server_ip_edit.text.strip_edges()
	server_port = int(port_spin.value)

	if server_ip.is_empty():
		status_label.text = "Server IP is empty."
		return

	var err := _udp.set_dest_address(server_ip, server_port)
	if err == OK:
		status_label.text = "UDP %s:%d | -- Hz" % [server_ip, server_port]
		_show_touch_surface()
	else:
		status_label.text = "Failed to set UDP target: %s" % error_string(err)


func _save_layout() -> void:
	var cfg := ConfigFile.new()
	cfg.set_value("layout", "area_x", touch_area.global_position.x)
	cfg.set_value("layout", "area_y", touch_area.global_position.y)
	cfg.set_value("layout", "area_w", touch_area.size.x)
	cfg.set_value("layout", "area_h", touch_area.size.y)
	cfg.save(LAYOUT_PATH)


func _load_layout() -> void:
	var cfg := ConfigFile.new()
	if cfg.load(LAYOUT_PATH) != OK:
		return
	touch_area.global_position = Vector2(
		cfg.get_value("layout", "area_x", touch_area.global_position.x),
		cfg.get_value("layout", "area_y", touch_area.global_position.y)
	)
	touch_area.size = Vector2(
		cfg.get_value("layout", "area_w", touch_area.size.x),
		cfg.get_value("layout", "area_h", touch_area.size.y)
	)


func _show_settings() -> void:
	settings_layer.visible = true
	touch_layer.visible = false
	_is_touching = false
	_active_touch_index = -1
	_edit_action = ""
	_edit_touch_index = -1


func _show_touch_surface() -> void:
	settings_layer.visible = false
	touch_layer.visible = true
	_set_edit_mode(true)
	_clamp_touch_area_to_viewport()


func _set_edit_mode(enabled: bool) -> void:
	_edit_mode = enabled
	edit_toggle.button_pressed = enabled
	center_handle.visible = enabled
	resize_handle.visible = enabled
	center_handle.disabled = not enabled
	resize_handle.disabled = not enabled
	edit_toggle.text = "Play Mode" if enabled else "Edit Area"
	_edit_action = ""
	_edit_touch_index = -1
	_sync_edit_handles()
	# Save layout when leaving edit mode
	if not enabled:
		_save_layout()


func _clamp_touch_area_to_viewport() -> void:
	var viewport_size := get_viewport_rect().size
	if viewport_size.x <= 0.0 or viewport_size.y <= 0.0:
		return

	var clamped_size := Vector2(
		clampf(touch_area.size.x, MIN_AREA_SIZE.x, viewport_size.x),
		clampf(touch_area.size.y, MIN_AREA_SIZE.y, viewport_size.y)
	)
	var max_position := viewport_size - clamped_size
	var clamped_position := Vector2(
		clampf(touch_area.global_position.x, 0.0, max_position.x),
		clampf(touch_area.global_position.y, 0.0, max_position.y)
	)

	touch_area.size = clamped_size
	touch_area.global_position = clamped_position
	_sync_edit_handles()


func _sync_edit_handles() -> void:
	if not is_node_ready():
		return

	var area_rect := _get_touch_area_rect()
	center_handle.size = HANDLE_SIZE
	center_handle.global_position = area_rect.position + (area_rect.size - HANDLE_SIZE) * 0.5
	resize_handle.size = RESIZE_HANDLE_SIZE
	resize_handle.global_position = area_rect.end - RESIZE_HANDLE_SIZE


func _get_touch_area_rect() -> Rect2:
	return Rect2(touch_area.global_position, touch_area.size)


func _get_control_rect(control: Control) -> Rect2:
	return Rect2(control.global_position, control.size)


func _is_over_toolbar(position: Vector2) -> bool:
	return _get_control_rect(edit_toggle).has_point(position) or _get_control_rect(back_button).has_point(position)
