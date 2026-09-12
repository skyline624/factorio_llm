local U = require("scripts.util")
local Actor = require("scripts.actor")
local Visibility = require("scripts.visibility")
local M = {}

local function box(value)
  return {min = U.copy(value.left_top), max = U.copy(value.right_bottom)}
end

local function mask(value)
  local layers = {}
  for name in pairs(value.layers) do layers[#layers + 1] = name end
  table.sort(layers)
  return {layers = layers, tilesOnly = value.colliding_with_tiles_only or false,
    ignoreSameMask = value.not_colliding_with_itself or false, tileTransitions = value.consider_tile_transitions or false}
end

local function prototype(value)
  local result = {name = value.name, type = value.type, collisionBox = box(value.collision_box),
    mask = mask(value.collision_mask), tileWidth = value.tile_width, tileHeight = value.tile_height}
  if value.type == "resource" then result.resourceCategory = value.resource_category end
  if value.type == "mining-drill" then
    result.miningRadius = value.mining_drill_radius
    local output = value.vector_to_place_result
    if output then result.miningOutput = {x = output[1], y = output[2]} end
    result.resourceCategories = value.resource_categories
    result.fuelCategories = value.burner_prototype and value.burner_prototype.fuel_categories
  end
  return result
end

function M.observe(args)
  local c = Actor.get()
  U.check(c ~= nil, "actor_dead", "Wait for the character to respawn")
  local radius = U.number(args.radius, "radius", 4, 48, 32, true)
  local x0, y0 = math.floor(c.position.x) - radius, math.floor(c.position.y) - radius
  local x1, y1 = math.floor(c.position.x) + radius + 1, math.floor(c.position.y) + radius + 1
  local area = {{x0, y0}, {x1, y1}}
  local result = {scope = Actor.scope(), collectedTick = game.tick, surfaceIndex = c.surface.index,
    bounds = {min = {x = x0, y = y0}, max = {x = x1, y = y1}},
    actor = {id = U.entity_id(c), name = c.name, position = U.copy(c.position),
      buildDistance = c.build_distance, reachDistance = c.reach_distance, controlMode = Actor.state().controlMode},
    prototypes = {[c.name] = prototype(c.prototype)}, tilePrototypes = {}, rows = {}, entities = {}, items = {},
    coverage = {atomic = true, complete = true, visibility = "current-character-local-area", radius = radius}}
  U.check(args.items == nil or type(args.items) == "table", "invalid_arguments", "items must be an array")
  for index, name in pairs(args.items or {}) do
    U.number(index, "item index", 1, 16, nil, true)
    U.string(name, "item")
    local item = prototypes.item[name]
    U.check(item and item.place_result, "not_buildable", "Requested item does not place an entity")
    result.items[name] = {entityName = item.place_result.name, stackSize = item.stack_size}
    result.prototypes[item.place_result.name] = prototype(item.place_result)
  end
  for y = y0, y1 - 1 do
    local start, name = x0, nil
    for x = x0, x1 do
      local tile = x < x1 and c.surface.get_tile(x, y)
      local next_name = tile and tile.name
      if next_name ~= name then
        if name then result.rows[#result.rows + 1] = {x = start, y = y, length = x - start, name = name} end
        start, name = x, next_name
        if tile and not result.tilePrototypes[name] then result.tilePrototypes[name] = mask(tile.prototype.collision_mask) end
      end
    end
  end
  local found = c.surface.find_entities_filtered{area = area, limit = 20001}
  U.check(#found <= 20000, "spatial_snapshot_too_large", "Spatial entity budget exceeded; no partial map returned")
  for _, entity in ipairs(found) do
    if Visibility.is_visible(c, entity) then
      result.prototypes[entity.name] = result.prototypes[entity.name] or prototype(entity.prototype)
      local value = {id = U.entity_id(entity), name = entity.name, position = U.copy(entity.position),
        bounds = box(entity.bounding_box), direction = entity.direction, force = entity.force.name}
      if entity.type == "resource" then value.amount = entity.amount end
      if entity.type == "mining-drill" then
        value.dropPosition = U.copy(entity.drop_position)
        local target = entity.drop_target
        if target and Visibility.is_visible(c, target) then value.dropTargetId = U.entity_id(target) end
      end
      result.entities[#result.entities + 1] = value
    end
  end
  table.sort(result.entities, function(a, b) return a.id < b.id end)
  return result
end

function M.validate_placement(args)
  local c = Actor.get()
  U.check(c ~= nil, "actor_dead", "Wait for the character to respawn")
  U.check(U.canonical(args.scope) == U.canonical(Actor.scope()), "stale_scope", "Actor scope changed")
  local item = prototypes.item[U.string(args.item, "item")]
  U.check(item and item.place_result, "not_buildable", "Item does not place an entity")
  U.check(type(args.candidates) == "table" and #args.candidates >= 1 and #args.candidates <= 100,
    "invalid_arguments", "Supply 1 to 100 candidate positions")
  local result = {scope = Actor.scope(), collectedTick = game.tick, item = args.item, candidates = {}}
  for index, candidate in ipairs(args.candidates) do
    local position = U.position(candidate.position)
    local direction = U.number(candidate.direction, "direction", 0, 12, 0, true)
    U.check(direction % 4 == 0, "invalid_direction", "Cardinal placement direction required")
    -- This read must not probe placements beyond current normal visibility.
    U.check(Visibility.is_visible(c, {position = position, surface = c.surface}), "position_not_visible",
      "Candidate is outside current visibility")
    result.candidates[#result.candidates + 1] = {index = index, position = position, direction = direction,
      allowed = c.surface.can_place_entity{name = item.place_result.name, position = position, direction = direction,
        force = c.force, build_check_type = defines.build_check_type.manual},
      inReach = U.distance(c.position, position) <= c.build_distance}
  end
  return result
end

return M
