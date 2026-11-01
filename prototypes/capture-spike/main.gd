extends Node2D
# THROWAWAY capture spike. Hardcoded on purpose.

const W := 1280.0
const H := 720.0
const MARGIN := 20.0
const COLS := 11
const ROWS := 8
const MOVE_DELAY := 3.0    # seconds an order shows its arrow before the hop
const BATTLE_SLOW := 0.4   # hop speed multiplier while in a fight (enter or leave)
const WIN_FRAC := 0.75
const INCOME_TILE := 0.2     # supply per second per owned tile
const DEPOSIT_MULT := 5.0
const STACK_COST := 20.0
const NEW_STACK_STR := 5
const DEPOSITS := 5

var seeds: Array[Vector2] = []
var polys: Array[PackedVector2Array] = []
var nbrs: Array = []
var owner_of: PackedInt32Array = []
var stacks: Array = []                # dictionaries
var t := 0.0
var paused := false
var state := 0                        # 0 play, 1 win, 2 lose
var deposit: PackedByteArray = []
var supply := [0.0, 0.0, 0.0]
var capital := [-1, -1, -1]
var selected = null
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
	var pt := nearest(Vector2(150, 200))
	var et := nearest(Vector2(W - 150, H - 120))
	owner_of[pt] = 1
	owner_of[et] = 2
	capital = [-1, pt, et]
	deposit = PackedByteArray()
	deposit.resize(n)
	var placed := 0
	while placed < DEPOSITS:
		var d := rng.randi() % n
		if deposit[d] == 0 and d != pt and d != et:
			deposit[d] = 1
			placed += 1
	supply = [0.0, 0.0, 0.0]
	stacks = [make_stack(1, pt, 10), make_stack(2, et, 8)]
	selected = stacks[0]
	t = 0.0
	state = 0
	paused = false

func make_stack(side: int, tile: int, strength: int) -> Dictionary:
	return {"side": side, "tile": tile, "target": -1, "timer": 0.0, "path": [], "str": strength, "hp": float(strength), "wait": 1.0}

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
		if tl < 0:
			return
		if Input.is_key_pressed(KEY_SHIFT) and selected != null and stacks.has(selected):
			# shift+click: extend the route from wherever it currently ends
			var q: Dictionary = selected
			var origin: int = q.tile
			if not q.path.is_empty():
				origin = q.path[q.path.size() - 1]
			elif q.target != -1:
				origin = q.target
			q.path.append_array(find_path(origin, tl))
			return
		# clicking a tile with your own stack selects it (repeat clicks cycle through a pile)
		var here := []
		for o in stacks:
			if o.side == 1 and o.tile == tl:
				here.append(o)
		if not here.is_empty() and (selected == null or selected.tile != tl or here.size() > 1):
			var idx := here.find(selected)
			selected = here[(idx + 1) % here.size()]
			return
		if selected != null and stacks.has(selected):
			var s: Dictionary = selected
			s.path = find_path(s.tile, tl)
			# keep a pending hop only if the new route still starts with it
			if s.target != -1 and (s.path.is_empty() or s.path[0] != s.target):
				s.target = -1
			elif s.target != -1:
				s.path.pop_front()
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
	# enemy AI: each idle stack marches on the nearest tile the enemy does not own
	for s in stacks:
		if s.side == 2 and s.target == -1 and s.path.is_empty():
			s.wait -= dt
			if s.wait <= 0.0:
				s.path = path_to_nearest_foreign(s.tile, 2)
				s.wait = rng.randf_range(0.5, 2.0)
	# battles: start as soon as a stack begins moving into an enemy tile
	for i in range(stacks.size()):
		for j in range(i + 1, stacks.size()):
			var a = stacks[i]
			var b = stacks[j]
			if fighting(a, b):
				a.hp -= dt * b.str * 0.12
				b.hp -= dt * a.str * 0.12
	var dead := []
	for s in stacks:
		if s.hp <= 0.0:
			dead.append(s)
	for s in dead:
		stacks.erase(s)
	# friendly stacks that are both idle on one tile merge into a single bigger stack
	var merged := true
	while merged:
		merged = false
		for i in range(stacks.size()):
			for j in range(i + 1, stacks.size()):
				var a = stacks[i]
				var b = stacks[j]
				if a.side == b.side and a.tile == b.tile and a.target == -1 and b.target == -1 and a.path.is_empty() and b.path.is_empty():
					a.str += b.str
					a.hp += b.hp
					if selected == b:
						selected = a
					stacks.remove_at(j)
					merged = true
					break
			if merged:
				break
	if selected != null and not stacks.has(selected):
		selected = null
		for o in stacks:
			if o.side == 1:
				selected = o
				break
	# economy: owned tiles make supply; supply buys a new stack at the capital
	for side in [1, 2]:
		var inc := 0.0
		for i in seeds.size():
			if owner_of[i] == side:
				inc += INCOME_TILE * (DEPOSIT_MULT if deposit[i] == 1 else 1.0)
		supply[side] += inc * dt
		if supply[side] >= STACK_COST:
			var at: int = capital[side]
			if owner_of[at] != side:
				var own := []
				for i in seeds.size():
					if owner_of[i] == side:
						own.append(i)
				if own.is_empty():
					continue
				at = own[rng.randi() % own.size()]
			supply[side] -= STACK_COST
			var ns := make_stack(side, at, NEW_STACK_STR)
			stacks.append(ns)
	# instant capture: alone on a tile you do not own
	for s in stacks:
		var tl: int = s.tile
		var enemy_here := false
		for o in stacks:
			if o.side != s.side and o.tile == tl:
				enemy_here = true
		if not enemy_here:
			owner_of[tl] = s.side
	var mine := 0
	for o in owner_of:
		if o == 1:
			mine += 1
	var theirs := 0
	for o in owner_of:
		if o == 2:
			theirs += 1
	if float(mine) / seeds.size() >= WIN_FRAC:
		state = 1
	elif mine == 0 or float(theirs) / seeds.size() >= WIN_FRAC:
		state = 2

func path_to_nearest_foreign(from: int, side: int) -> Array:
	var prev := {from: -1}
	var q := [from]
	var goal := -1
	while not q.is_empty():
		var c: int = q.pop_front()
		if c != from and owner_of[c] != side:
			goal = c
			break
		var nb: Array = nbrs[c].duplicate()
		nb.shuffle()
		for n in nb:
			if not prev.has(n):
				prev[n] = c
				q.append(n)
	if goal == -1:
		return []
	var path := []
	var c: int = goal
	while c != from:
		path.push_front(c)
		c = prev[c]
	return path

func fighting(a: Dictionary, b: Dictionary) -> bool:
	if a.side == b.side:
		return false
	return a.tile == b.tile or a.target == b.tile or b.target == a.tile

func engaged(s: Dictionary) -> bool:
	for o in stacks:
		if o != s and fighting(s, o):
			return true
	return false

func advance(s: Dictionary, dt: float) -> void:
	if s.target == -1:
		if s.path.is_empty():
			return
		s.target = s.path.pop_front()
		s.timer = 0.0
	s.timer += dt * (BATTLE_SLOW if engaged(s) else 1.0)
	if s.timer >= MOVE_DELAY:
		s.tile = s.target
		s.target = -1

func stack_pos(s: Dictionary) -> Vector2:
	return centroid(s.tile)

func draw_arrow(from: Vector2, to: Vector2, prog: float, col: Color) -> void:
	var dir := (to - from).normalized()
	var perp := Vector2(-dir.y, dir.x)
	var a := from + dir * 20.0
	var b := to - dir * 20.0
	draw_line(a, b, Color(col, 0.35), 6.0)
	var tip := a.lerp(b, prog)
	draw_line(a, tip, col, 6.0)
	draw_colored_polygon(PackedVector2Array([b + dir * 14.0, b - dir * 4.0 + perp * 12.0, b - dir * 4.0 - perp * 12.0]), Color(col, 0.35 + 0.65 * prog))

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
		var outline := PackedVector2Array(polys[i])
		outline.append(polys[i][0])
		draw_polyline(outline, Color(0, 0, 0, 0.8), 2.0)
	for i in polys.size():
		var c := centroid(i)
		if deposit[i] == 1:
			draw_colored_polygon(PackedVector2Array([c + Vector2(0, -9), c + Vector2(9, 0), c + Vector2(0, 9), c + Vector2(-9, 0)]), Color(1, 0.85, 0.25))
		if i == capital[1] or i == capital[2]:
			draw_rect(Rect2(c - Vector2(11, 11), Vector2(22, 22)), Color.WHITE, false, 3.0)
	if hover >= 0:
		var o := PackedVector2Array(polys[hover])
		o.append(polys[hover][0])
		draw_polyline(o, Color(1, 1, 1, 0.8), 2.0)
	# player path
	if selected != null and stacks.has(selected):
		var p0: Dictionary = selected
		var prev := centroid(p0.target) if p0.target != -1 else stack_pos(p0)
		for tl in p0.path:
			draw_line(prev, centroid(tl), Color(1, 1, 1, 0.5), 2.0)
			prev = centroid(tl)
	for s in stacks:
		if s.target != -1:
			draw_arrow(centroid(s.tile), centroid(s.target), s.timer / MOVE_DELAY, Color(1, 0.9, 0.2) if engaged(s) else (Color(1, 1, 1) if s.side == 1 else Color(1, 0.7, 0.2)))
	var font := ThemeDB.fallback_font
	for s in stacks:
		var pos := stack_pos(s)
		var rad := 13.0 + minf(s.hp, 30.0) * 0.3
		draw_circle(pos, rad + 2, Color.BLACK)
		draw_circle(pos, rad, side_color(s.side).lightened(0.25))
		draw_string(font, pos + Vector2(-8, 6), str(ceili(s.hp)), HORIZONTAL_ALIGNMENT_CENTER, 16, 16, Color.BLACK)
		if s == selected:
			draw_arc(pos, 19, 0, TAU, 24, Color.WHITE, 2.0)
		var contested := engaged(s)
		if contested:
			draw_arc(pos, 22, 0, TAU, 24, Color(1, 0.9, 0.2), 3.0)
	var mine := 0
	var theirs := 0
	for o in owner_of:
		if o == 1:
			mine += 1
		elif o == 2:
			theirs += 1
	var txt := "Blue %d  Red %d  / %d (need %d)   Supply %d/%d   Red supply %d   t=%ds%s" % [mine, theirs, seeds.size(), ceili(seeds.size() * WIN_FRAC), int(supply[1]), int(STACK_COST), int(supply[2]), int(t), "   PAUSED" if paused else ""]
	draw_string(font, Vector2(24, 36), txt, HORIZONTAL_ALIGNMENT_LEFT, -1, 22)
	if state != 0:
		var msg := "YOU WIN" if state == 1 else "DEFEATED"
		draw_string(font, Vector2(W / 2 - 160, H / 2), msg + "  (R to restart)", HORIZONTAL_ALIGNMENT_LEFT, -1, 32, Color.WHITE)
