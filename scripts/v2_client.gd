extends Control

const MIN_AREA_SIZE := Vector2(160.0, 120.0)
const HANDLE_SIZE := Vector2(140.0, 70.0)
const RESIZE_HANDLE_SIZE := Vector2(72.0, 60.0)
const LAYOUT_PATH := "user://v2_layout.cfg"
const TCP_PORT := 42425

@export var server_ip := "10.48.90.189"

@onready var settings_layer: MarginContainer = $SettingsLayer
@onready var touch_layer: Control = $TouchLayer
@onready var status_label: Label = $SettingsLayer/Panel/VBox/Status
@onready var server_ip_edit: LineEdit = $SettingsLayer/Panel/VBox/ServerRow/ServerIp
@onready var tap_to_click_check: CheckButton = $SettingsLayer/Panel/VBox/TapToClick
@onready var apply_button: Button = $SettingsLayer/Panel/VBox/ApplyButton
@onready var touch_area: ColorRect = $TouchLayer/TouchArea
@onready var center_handle: Button = $TouchLayer/CenterHandle
@onready var resize_handle: Button = $TouchLayer/ResizeHandle
@onready var edit_toggle: Button = $TouchLayer/Toolbar/EditToggle
@onready var back_button: Button = $TouchLayer/Toolbar/BackButton

var _tcp := StreamPeerTCP.new()
var _enable_pen_clicks := true
var _edit_mode := true
var _edit_action := ""
var _edit_touch_index := -1
var _edit_start_touch := Vector2.ZERO
var _edit_start_area_position := Vector2.ZERO
var _edit_start_area_size := Vector2.ZERO
var _connected := false


func _ready() -> void:
	Input.use_accumulated_input = false
	server_ip_edit.text = server_ip
	tap_to_click_check.button_pressed = _enable_pen_clicks
	tap_to_click_check.toggled.connect(func(t): _enable_pen_clicks = t)
	apply_button.pressed.connect(_apply_and_connect)
	back_button.pressed.connect(_show_settings)
	edit_toggle.toggled.connect(_set_edit_mode)
	_load_layout()
	_show_settings()
	_set_edit_mode(true)
	call_deferred("_clamp_touch_area_to_viewport")


func _process(_delta: float) -> void:
	if _tcp.get_status() == StreamPeerTCP.STATUS_CONNECTED:
		_tcp.poll()
	elif _connected:
		# Lost connection
		_connected = false
		status_label.text = "Disconnected. Re-apply to reconnect."


func _notification(what: int) -> void:
	if what == NOTIFICATION_RESIZED:
		_clamp_touch_area_to_viewport()


func _input(event: InputEvent) -> void:
	if not touch_layer.visible:
		return

	if event is InputEventMouseMotion:
		if _edit_mode and (event.button_mask & MOUSE_BUTTON_MASK_LEFT) != 0:
			_handle_edit_drag(99, event.position)
		return

	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT:
		if _edit_mode:
			_handle_edit_touch(99, event.pressed, event.position)
		return

	if event is InputEventScreenTouch:
		if _edit_mode:
			_handle_edit_touch(event.index, event.pressed, event.position)
		return

	if event is InputEventScreenDrag:
		if _edit_mode:
			_handle_edit_drag(event.index, event.position)


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


func _apply_and_connect() -> void:
	server_ip = server_ip_edit.text.strip_edges()
	if server_ip.is_empty():
		status_label.text = "Server IP is empty."
		return

	_tcp.disconnect_from_host()
	_connected = false

	var err := _tcp.connect_to_host(server_ip, TCP_PORT)
	if err != OK:
		status_label.text = "Connect error: %s" % error_string(err)
		return

	status_label.text = "Connecting to %s..." % server_ip
	# Wait for connection then send
	await get_tree().create_timer(0.3).timeout
	_tcp.poll()

	if _tcp.get_status() == StreamPeerTCP.STATUS_CONNECTED:
		_connected = true
		_send_mapping()
		_show_touch_surface()
	else:
		status_label.text = "Failed to connect. Is the server running?"


func _send_mapping() -> void:
	if _tcp.get_status() != StreamPeerTCP.STATUS_CONNECTED:
		return

	var vp_size := get_viewport_rect().size
	if vp_size.x <= 0.0 or vp_size.y <= 0.0:
		return

	var area_pos := touch_area.global_position
	var area_size := touch_area.size

	var buf := PackedByteArray()
	buf.resize(17)
	buf.encode_float(0, area_pos.x / vp_size.x)
	buf.encode_float(4, area_pos.y / vp_size.y)
	buf.encode_float(8, area_size.x / vp_size.x)
	buf.encode_float(12, area_size.y / vp_size.y)
	buf.encode_u8(16, 1 if _enable_pen_clicks else 0)
	_tcp.put_data(buf)


func _show_settings() -> void:
	_tcp.disconnect_from_host()
	_connected = false
	settings_layer.visible = true
	touch_layer.visible = false


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
	if not enabled:
		_save_layout()
		# Re-send updated mapping when entering play mode
		_send_mapping()


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


func _clamp_touch_area_to_viewport() -> void:
	var vp := get_viewport_rect().size
	if vp.x <= 0.0 or vp.y <= 0.0:
		return
	var clamped_size := Vector2(
		clampf(touch_area.size.x, MIN_AREA_SIZE.x, vp.x),
		clampf(touch_area.size.y, MIN_AREA_SIZE.y, vp.y)
	)
	var max_pos := vp - clamped_size
	touch_area.size = clamped_size
	touch_area.global_position = Vector2(
		clampf(touch_area.global_position.x, 0.0, max_pos.x),
		clampf(touch_area.global_position.y, 0.0, max_pos.y)
	)
	_sync_edit_handles()


func _sync_edit_handles() -> void:
	if not is_node_ready():
		return
	var area_rect := Rect2(touch_area.global_position, touch_area.size)
	center_handle.size = HANDLE_SIZE
	center_handle.global_position = area_rect.position + (area_rect.size - HANDLE_SIZE) * 0.5
	resize_handle.size = RESIZE_HANDLE_SIZE
	resize_handle.global_position = area_rect.end - RESIZE_HANDLE_SIZE


func _get_control_rect(control: Control) -> Rect2:
	return Rect2(control.global_position, control.size)
