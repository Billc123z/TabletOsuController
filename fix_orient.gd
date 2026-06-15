extends SceneTree
func _init():
    ProjectSettings.set_setting("display/window/handheld/orientation", 4)
    ProjectSettings.save()
    quit()
