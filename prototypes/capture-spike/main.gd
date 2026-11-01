extends Node2D
# THROWAWAY capture spike. Hardcoded on purpose.

const W := 1280.0
const H := 720.0
const MARGIN := 20.0
const COLS := 11
const ROWS := 8
const SPEED := 70.0        # px/s
const CAPTURE_TIME := 3.0
const WIN_FRAC := 0.75

var seeds: Array[Vector2] = []
var polys: Array[PackedVector2Array] = []
var nbrs: Array = []
var owner_of: PackedInt32Array = []
var cap_side: PackedInt32Array = []   # who is currently capturing
var cap_prog: PackedFloat32Array = []
var stacks: Array = []                # dictionaries
var t := 0.0
var paused := false
var state := 0                        # 0 play, 1 win, 2 lose
var respawn_in := 0.0
var rng := RandomNumberGenerator.new()
var hover := -1

func _ready() -> void:
	start()
	var tot := 0
	var mn := 99
	for nb in nbrs:
		tot += nb.size()
		mn = mini(mn, nb.size())
	var reach := 0
	for i in seeds.size():
		if i == 0 or not find_path(0, i).is_empty():
			reach += 1
	print("tiles=%d avg_nbrs=%.2f min_nbrs=%d reachable=%d" % [seeds.size(), float(tot) / nbrs.size(), mn, reach])

func start() -> void:
	rng.randomize()
	seeds.clear()
	polys.clear()
	nbrs.clear()
	var cw := (W - MARGIN * 2) / COLS
	var ch := (H - MARGIN * 2 - 40) / ROWS
	for r in ROWS:
		for c in COLS:
			var p := Vector2(MARGIN + (c + 0.5) * cw + rng.randf_range(-0.32, 0.32) * cw,
				MARGIN + 40 + (r + 0.5) * ch + rng.randf_range(-0.32, 0.32) * ch)
			seeds.append(p)
	var box := Rect2(MARGIN, MARGIN + 40, W - MARGIN * 2, H - MARGIN * 2 - 40)
	for i in seeds.size():
		var poly := PackedVector2Array([box.position, Vector2(box.end.x, box.position.y), box.end, Vector2(box.position.x, box.end.y)])
		for j in seeds.size():
			if i == j or seeds[i].distance_to(seeds[j]) > cw * 3.2:
				continue
			poly = clip(poly, seeds[i], seeds[j])
		polys.append(poly)
	for i in seeds.size():
		var nb := []
		for j in seeds.size():
			if i != j and shares_edge(i, j):
				nb.append(j)
		nbrs.append(nb)
	var n := seeds.size()
	owner_of = PackedInt32Array()
	owner_of.resize(n)
	cap_side = PackedInt32Array()
	cap_side.resize(n)
	cap_prog = PackedFloat32Array()
	cap_prog.resize(n)
	var pt := nearest(Vector2(150, 200))
	var et := nearest(Vector2(W - 150, H - 120))
	owner_of[pt] = 1
	owner_of[et] = 2
	stacks = [make_stack(1, pt, 10), make_stack(2, et, 8)]
	t = 0.0
	state = 0
	respawn_in = 0.0
	paused = false

func make_stack(side: int, tile: int, strength: int) -> Dictionary:
	return {"side": side, "tile": tile, "from": tile, "to": tile, "k": 1.0, "path": [], "str": strength, "hp": float(strength), "wait": 0.0}

func clip(poly: PackedVector2Array, a: Vector2, b: Vector2) -> PackedVector2Array:
	var m := (a + b) * 0.5
	var nrm := b - a
	var out := PackedVector2Array()
	for k in poly.size():
		var p := poly[k]
		var q := poly[(k + 1) % poly.size()]
		var dp := (p - m).dot(nrm)
		var dq := (q - m).dot(nrm)
		if dp <= 0:
			out.append(p)
		if (dp <= 0) != (dq <= 0):
			out.append(p + (q - p) * (dp / (dp - dq)))
	return out

func shares_edge(i: int, j: int) -> bool:
	var m := (seeds[i] + seeds[j]) * 0.5
	var nrm := (seeds[j] - seeds[i]).normalized()
	var on := 0
	for v in polys[i]:
		if absf((v - m).dot(nrm)) < 0.5:
			on += 1
	return on >= 2

func centroid(i: int) -> Vector2:
	var s := Vector2.ZERO
	for v in polys[i]:
		s += v
	return s / polys[i].size()

func nearest(p: Vector2) -> int:
	var best := 0
	for i in seeds.size():
		if seeds[i].distance_to(p) < seeds[best].distance_to(p):
			best = i
	return best

func tile_at(p: Vector2) -> int:
	for i in polys.size():
		if Geometry2D.is_point_in_polygon(p, polys[i]):
			return i
	return -1

func find_path(a: int, b: int) -> Array:
	var prev := {a: -1}
	var q := [a]
	while not q.is_empty():
		var c: int = q.pop_front()
		if c == b:
			break
		for n in nbrs[c]:
			if not prev.has(n):
				prev[n] = c
				q.append(n)
	if not prev.has(b):
		return []
	var path := []
	var c: int = b
	while c != a:
		path.push_front(c)
		c = prev[c]
	return path

func _unhandled_input(e: InputEvent) -> void:
	if e is InputEventMouseButton and e.pressed and e.button_index == MOUSE_BUTTON_LEFT and state == 0:
		var tl := tile_at(e.position)
		var s: Dictionary = stacks[0]
		if tl >= 0 and s.side == 1:
			# re-path from the tile we are heading to, so a mid-edge click doesn't teleport
			var origin: int = s.to if s.k < 1.0 else s.tile
			s.path = find_path(origin, tl)
	elif e is InputEventMouseMotion:
		hover = tile_at(e.position)
	elif e is InputEventKey and e.pressed:
		if e.keycode == KEY_SPACE:
			paused = not paused
		elif e.keycode == KEY_R:
			start()

func _process(dt: float) -> void:
	if not paused and state == 0:
		step(dt)
	queue_redraw()

func step(dt: float) -> void:
	t += dt
	for s in stacks:
		advance(s, dt)
	# enemy random walk
	for s in stacks:
		if s.side == 2 and s.k >= 1.0 and s.path.is_empty():
			s.wait -= dt
			if s.wait <= 0.0:
				var nb: Array = nbrs[s.tile]
				s.path = [nb[rng.randi() % nb.size()]]
				s.wait = rng.randf_range(0.0, 2.5)  # sometimes lingers
	# battles: same tile, opposite sides
	for i in range(stacks.size()):
		for j in range(i + 1, stacks.size()):
			var a = stacks[i]
			var b = stacks[j]
			if a.side != b.side and a.tile == b.tile:
				a.hp -= dt * b.str * 0.12
				b.hp -= dt * a.str * 0.12
	var dead := []
	for s in stacks:
		if s.hp <= 0.0:
			dead.append(s)
	for s in dead:
		stacks.erase(s)
		if s.side == 1:
			state = 2
		else:
			respawn_in = 20.0
	if respawn_in > 0.0:
		respawn_in -= dt
		if respawn_in <= 0.0:
			stacks.append(make_stack(2, rng.randi() % seeds.size(), 8))
	# capture
	for s in stacks:
		var tl: int = s.tile
		var enemy_here := false
		for o in stacks:
			if o.side != s.side and o.tile == tl:
				enemy_here = true
		if enemy_here or owner_of[tl] == s.side:
			continue
		if cap_side[tl] != s.side:
			cap_prog[tl] -= dt / CAPTURE_TIME
			if cap_prog[tl] <= 0.0:
				cap_prog[tl] = 0.0
				cap_side[tl] = s.side
		else:
			cap_prog[tl] += dt / CAPTURE_TIME
			if cap_prog[tl] >= 1.0:
				owner_of[tl] = s.side
				cap_prog[tl] = 0.0
	# progress decays on unattended tiles
	for i in seeds.size():
		if cap_prog[i] > 0.0:
			var held := false
			for s in stacks:
				if s.tile == i and s.side == cap_side[i]:
					held = true
			if not held:
				cap_prog[i] = maxf(0.0, cap_prog[i] - dt / CAPTURE_TIME * 0.5)
	var mine := 0
	for o in owner_of:
		if o == 1:
			mine += 1
	if float(mine) / seeds.size() >= WIN_FRAC:
		state = 1

func advance(s: Dictionary, dt: float) -> void:
	var left := dt
	while left > 0.0:
		if s.k >= 1.0:
			if s.path.is_empty():
				return
			s.from = s.tile
			s.to = s.path.pop_front()
			s.k = 0.0
		var d := centroid(s.from).distance_to(centroid(s.to))
		var step_k := left * SPEED / maxf(d, 1.0)
		if s.k + step_k >= 1.0:
			left -= (1.0 - s.k) * d / SPEED
			s.k = 1.0
			s.tile = s.to
		else:
			s.k += step_k
			left = 0.0
			s.tile = s.to if s.k >= 0.5 else s.from

func stack_pos(s: Dictionary) -> Vector2:
	return centroid(s.from).lerp(centroid(s.to), s.k)

func side_color(side: int) -> Color:
	match side:
		1: return Color(0.25, 0.55, 1.0)
		2: return Color(1.0, 0.35, 0.3)
	return Color(0.32, 0.34, 0.38)

func shrink(poly: PackedVector2Array, c: Vector2, f: float) -> PackedVector2Array:
	var out := PackedVector2Array()
	for v in poly:
		out.append(c + (v - c) * f)
	return out

func _draw() -> void:
	draw_rect(Rect2(0, 0, W, H), Color(0.08, 0.09, 0.11))
	for i in polys.size():
		var base := side_color(owner_of[i])
		draw_colored_polygon(polys[i], base.darkened(0.45) if owner_of[i] != 0 else base)
		if cap_prog[i] > 0.0:
			var c := centroid(i)
			var f := sqrt(cap_prog[i])
			draw_colored_polygon(shrink(polys[i], c, f), side_color(cap_side[i]).darkened(0.1))
		var outline := PackedVector2Array(polys[i])
		outline.append(polys[i][0])
		draw_polyline(outline, Color(0, 0, 0, 0.8), 2.0)
	if hover >= 0:
		var o := PackedVector2Array(polys[hover])
		o.append(polys[hover][0])
		draw_polyline(o, Color(1, 1, 1, 0.8), 2.0)
	# player path
	if not stacks.is_empty() and stacks[0].side == 1:
		var p0: Dictionary = stacks[0]
		var prev := stack_pos(p0)
		for tl in p0.path:
			draw_line(prev, centroid(tl), Color(1, 1, 1, 0.5), 2.0)
			prev = centroid(tl)
	var font := ThemeDB.fallback_font
	for s in stacks:
		var pos := stack_pos(s)
		draw_circle(pos, 15, Color.BLACK)
		draw_circle(pos, 13, side_color(s.side).lightened(0.25))
		draw_string(font, pos + Vector2(-8, 6), str(ceili(s.hp)), HORIZONTAL_ALIGNMENT_CENTER, 16, 16, Color.BLACK)
		var contested := false
		for o in stacks:
			if o.side != s.side and o.tile == s.tile:
				contested = true
		if contested:
			draw_arc(pos, 22, 0, TAU, 24, Color(1, 0.9, 0.2), 3.0)
	var mine := 0
	var theirs := 0
	for o in owner_of:
		if o == 1:
			mine += 1
		elif o == 2:
			theirs += 1
	var txt := "Blue %d  Red %d  / %d    (need %d)    t=%ds%s" % [mine, theirs, seeds.size(), ceili(seeds.size() * WIN_FRAC), int(t), "   PAUSED" if paused else ""]
	draw_string(font, Vector2(24, 36), txt, HORIZONTAL_ALIGNMENT_LEFT, -1, 22)
	if state != 0:
		var msg := "YOU WIN" if state == 1 else "STACK DESTROYED"
		draw_string(font, Vector2(W / 2 - 160, H / 2), msg + "  (R to restart)", HORIZONTAL_ALIGNMENT_LEFT, -1, 32, Color.WHITE)
