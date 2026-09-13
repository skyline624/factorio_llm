local U = require("scripts.util")
local Actor = require("scripts.actor")
local Visibility = require("scripts.visibility")
local M = {}
local status_names = {}
for name, value in pairs(defines.entity_status) do status_names[value] = name end

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
    mask = mask(value.collision_mask), tileWidth = value.tile_width, tileHeight = value.tile_height,
    isElectric = value.electric_energy_source_prototype ~= nil}
  if value.type == "resource" then
    result.resourceCategory = value.resource_category
    result.miningTime = value.mineable_properties.mining_time
  end
  if value.type == "mining-drill" then
    result.miningRadius = value.mining_drill_radius
    result.miningSpeed = value.mining_speed
    local output = value.vector_to_place_result
    if output then result.miningOutput = {x = output[1], y = output[2]} end
    result.resourceCategories = value.resource_categories
    result.fuelCategories = value.burner_prototype and value.burner_prototype.fuel_categories
  end
  if value.type == "inserter" then
    local pickup, drop = value.inserter_pickup_position, value.inserter_drop_position
    result.inserterPickup = pickup and {x = pickup[1], y = pickup[2]}
    result.inserterDrop = drop and {x = drop[1], y = drop[2]}
  end
  if value.type == "transport-belt" then result.beltSpeed = value.belt_speed end
  if #value.fluidbox_prototypes > 0 then
    result.fluidBoxes = {}
    for _, fluidbox in ipairs(value.fluidbox_prototypes) do
      local connections = {}
      for index, connection in ipairs(fluidbox.pipe_connections) do
        connections[#connections + 1] = {index = index, type = connection.connection_type,
          direction = connection.direction, flowDirection = connection.flow_direction,
          positions = U.copy(connection.positions), categories = U.copy(connection.connection_category)}
      end
      result.fluidBoxes[#result.fluidBoxes + 1] = {index = fluidbox.index, productionType = fluidbox.production_type,
        filter = fluidbox.filter and fluidbox.filter.name, minimumTemperature = fluidbox.minimum_temperature,
        maximumTemperature = fluidbox.maximum_temperature, connections = connections}
    end
  end
  if value.type == "offshore-pump" then
    local offset = value.fluid_source_offset
    result.fluidSourceOffset = {x = offset[1], y = offset[2]}
  end
  if value.type == "electric-pole" then
    result.supplyArea = value.get_supply_area_distance("normal")
    result.maxWireDistance = value.get_max_wire_distance("normal")
  end
  if value.burner_prototype then
    result.fuelCategories = value.burner_prototype.fuel_categories
    result.burnerEffectivity = value.burner_prototype.effectivity
    result.energyPerTick = value.get_max_energy_usage("normal")
  end
  if #value.tile_buildability_rules > 0 then
    result.tileBuildability = {}
    for _, rule in ipairs(value.tile_buildability_rules) do
      result.tileBuildability[#result.tileBuildability + 1] = {area = box(rule.area),
        collidingTiles = mask(rule.colliding_tiles), requiredTiles = mask(rule.required_tiles)}
    end
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
      buildDistance = c.build_distance, reachDistance = c.reach_distance,
      resourceReachDistance = c.resource_reach_distance, controlMode = Actor.state().controlMode},
    prototypes = {[c.name] = prototype(c.prototype)}, tilePrototypes = {}, tileFluids = {}, rows = {}, entities = {}, items = {},
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
        if tile and not result.tilePrototypes[name] then
          result.tilePrototypes[name] = mask(tile.prototype.collision_mask)
          if tile.prototype.fluid then result.tileFluids[name] = tile.prototype.fluid.name end
        end
      end
    end
  end
  local found = c.surface.find_entities_filtered{area = area, limit = 20001}
  U.check(#found <= 20000, "spatial_snapshot_too_large", "Spatial entity budget exceeded; no partial map returned")
  for _, entity in ipairs(found) do
    if Visibility.is_visible(c, entity) then
      result.prototypes[entity.name] = result.prototypes[entity.name] or prototype(entity.prototype)
      local value = {id = U.entity_id(entity), name = entity.name, position = U.copy(entity.position),
        bounds = box(entity.bounding_box), boundsOrientation = entity.bounding_box.orientation or 0,
        direction = entity.direction, force = entity.force.name}
      if entity.type == "assembling-machine" or entity.type == "furnace" or entity.type == "inserter" then
        value.status = status_names[entity.status]
      end
      if entity.type == "resource" then value.amount = entity.amount end
      if entity.type == "mining-drill" or entity.type == "inserter" then
        value.dropPosition = U.copy(entity.drop_position)
        local target = entity.drop_target
        if target and Visibility.is_visible(c, target) then value.dropTargetId = U.entity_id(target) end
      end
      if entity.type == "inserter" then
        value.pickupPosition = U.copy(entity.pickup_position)
        local target = entity.pickup_target
        if target and Visibility.is_visible(c, target) then value.pickupTargetId = U.entity_id(target) end
      end
      if entity.type == "transport-belt" or entity.type == "underground-belt" or entity.type == "splitter" then
        value.beltConnections = {inputs = {}, outputs = {}}
        local neighbours = entity.belt_neighbours
        for _, side in ipairs{"inputs", "outputs"} do
          for _, target in pairs(neighbours[side]) do
            if target.valid and Visibility.is_visible(c, target) then
              value.beltConnections[side][#value.beltConnections[side] + 1] = U.entity_id(target)
            end
          end
          table.sort(value.beltConnections[side])
        end
      end
      if #entity.fluidbox > 0 then
        value.fluidConnections = {}
        for index = 1, #entity.fluidbox do
          for port, connection in ipairs(entity.fluidbox.get_pipe_connections(index)) do
            local filter = entity.fluidbox.get_filter(index)
            local target = connection.target and connection.target.owner
            local visible = target and target.valid and Visibility.is_visible(c, target)
            value.fluidConnections[#value.fluidConnections + 1] = {boxIndex = index, portIndex = port,
              position = U.copy(connection.position), targetPosition = U.copy(connection.target_position),
              targetEntityId = visible and U.entity_id(target) or nil,
              targetBoxIndex = visible and connection.target_fluidbox_index or nil,
              type = connection.connection_type, flowDirection = connection.flow_direction, filter = filter and filter.name}
          end
        end
      end
      if entity.type == "electric-pole" or entity.prototype.electric_energy_source_prototype then
        value.power = {energy = entity.energy, networkId = entity.electric_network_id,
          generatedLastTick = entity.type == "generator" and entity.energy_generated_last_tick or nil}
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
