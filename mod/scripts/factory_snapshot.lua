local U = require("scripts.util")
local Actor = require("scripts.actor")
local Recovery = require("scripts.recovery")
local Weapons = require("scripts.weapons")
local M = {}
local maximum_entities, maximum_records, lifetime = 20000, 150000, 3600
local belt_types = {["transport-belt"] = true, ["underground-belt"] = true, splitter = true,
  loader = true, ["loader-1x1"] = true, ["linked-belt"] = true}

local function stack_data(stack)
  if not stack or not stack.valid_for_read then return end
  local result = {name = stack.name, quality = stack.quality.name, count = stack.count, health = stack.health}
  if stack.prototype.type == "ammo" then result.ammo = stack.ammo end
  if stack.prototype.type == "tool" then result.durability = stack.durability end
  return result
end

local function stack_items(stack)
  local value = stack_data(stack)
  if not value then return {} end
  local key = value.name .. (value.quality ~= "normal" and "@" .. value.quality or "")
  return {[key] = value.count}
end

local function capacity_items(args)
  local result, seen = {}, {}
  U.check(args.capacityItems == nil or type(args.capacityItems) == "table", "invalid_arguments", "capacityItems must be an array")
  for index, name in pairs(args.capacityItems or {}) do
    U.number(index, "capacity item index", 1, 8, nil, true)
    U.string(name, "capacity item")
    U.check(prototypes.item[name] ~= nil and not seen[name], "invalid_arguments", "Unknown or repeated capacity item")
    seen[name], result[#result + 1] = true, name
  end
  table.sort(result)
  return result
end

local function capture(args)
  local s, c, force = Actor.state(), Actor.get(), Actor.force()
  U.check(force ~= nil, "handshake_required", "Create the actor before collecting factory state")
  local requested_capacity = capacity_items(args)
  local entities, roles = {}, {}
  -- Refresh only locally known own entities, never scan an undiscovered surface.
  if c then
    for _, entity in ipairs(c.surface.find_entities_filtered{position = c.position, radius = 32, force = force}) do
      if entity ~= c then s.known[U.entity_id(entity)] = entity end
    end
    entities[U.entity_id(c)], roles[U.entity_id(c)] = c, "actor"
  end
  for id, entity in pairs(s.known) do
    if entity.valid and entity.force == force then
      entities[id], roles[id] = entity, "factory"
    else s.known[id] = nil end
  end
  for id, record in pairs(Recovery.records()) do entities[id], roles[id] = record.entity, "corpse" end
  local ids = {}
  for id in pairs(entities) do ids[#ids + 1] = id end
  table.sort(ids)
  U.check(#ids <= maximum_entities, "snapshot_too_large", "Factory snapshot exceeds the entity budget; no partial snapshot returned")
  local records, by_id = {}, {}
  local function add(id, kind, owner, name, data)
    local previous = by_id[id]
    if previous then return previous end
    U.check(#records < maximum_records, "snapshot_too_large", "Factory snapshot exceeds the record budget")
    local record = {id = id, kind = kind, entityId = owner, name = name, data = data}
    records[#records + 1], by_id[id] = record, record
    return record
  end
  for _, id in ipairs(ids) do
    local entity = entities[id]
    local metadata = {role = roles[id], type = entity.type, surfaceIndex = entity.surface.index,
      position = U.copy(entity.position), direction = entity.direction, force = entity.force.name,
      inventories = {}, transportLines = {}, fluidStores = {}}
    local fuel = entity.get_fuel_inventory()
    if entity.type == "character" then
      local main = entity.get_main_inventory()
      metadata.mainInventoryId = "inventory:" .. id .. ":" .. main.index
    end
    if entity.type == "ammo-turret" then
      local ammunition = entity.get_inventory(defines.inventory.turret_ammo)
      local defense = Weapons.defense(entity)
      metadata.active, metadata.quality = entity.active, entity.quality.name
      metadata.ammoInventoryId = "inventory:" .. id .. ":" .. ammunition.index
      metadata.ammoRounds, metadata.defenseReady = U.ammo(ammunition), defense ~= nil
      metadata.defenseRange = defense and defense.range
    end
    if entity.burner then metadata.burnerRemainingJoules = entity.burner.remaining_burning_fuel end
    if fuel and fuel.valid then
      local owner = fuel.entity_owner
      U.check(owner and owner.valid and fuel.index, "inventory_identity_unavailable", "Fuel inventory identity is unavailable")
      metadata.fuelInventoryId = "inventory:" .. U.entity_id(owner) .. ":" .. fuel.index
    end
    add(id, "entity", id, entity.name, metadata)
    for index = 1, entity.get_max_inventory_index() do
      local inventory = entity.get_inventory(index)
      if inventory and inventory.valid then
        local owner = inventory.entity_owner
        -- The native owner/index identify shared inventories without summing aliases.
        U.check(owner and owner.valid and inventory.index, "inventory_identity_unavailable",
          "An inventory has no native entity owner/index; stock aggregation is refused")
        local owner_id = owner == entity and id or U.entity_id(owner)
        local inventory_id = "inventory:" .. owner_id .. ":" .. inventory.index
        metadata.inventories[#metadata.inventories + 1] = inventory_id
        if not by_id[inventory_id] then
          local bar = inventory.supports_bar() and inventory.get_bar() or #inventory + 1
          local data = {index = inventory.index, role = roles[id], items = U.inventory(inventory), slots = #inventory,
            usableSlots = math.min(#inventory, bar - 1), bar = bar, filters = {}, stacks = {}, capacityHints = {}}
          for slot = 1, #inventory do
            local stack = stack_data(inventory[slot])
            if stack then stack.slot = slot; data.stacks[#data.stacks + 1] = stack end
            if inventory.supports_filters() then
              local filter = inventory.get_filter(slot)
              if filter then data.filters[#data.filters + 1] = {slot = slot, filter = U.copy(filter)} end
            end
          end
          for _, item in ipairs(requested_capacity) do
            data.capacityHints[item] = {insertable = inventory.get_insertable_count{name = item, quality = "normal"},
              canInsertOne = inventory.can_insert{name = item, quality = "normal", count = 1}, certainty = "native-estimate"}
          end
          add(inventory_id, "inventory", owner_id, inventory.name or entity.get_inventory_name(index) or tostring(index), data)
        end
      end
    end
    if belt_types[entity.type] then
      for index = 1, entity.get_max_transport_line_index() do
        local line = entity.get_transport_line(index)
        if line.valid then
          local line_id = "transport:" .. id .. ":" .. index
          metadata.transportLines[#metadata.transportLines + 1] = line_id
          -- get_contents is scoped to this entity's section. line_equals compares
          -- internal engine segments and must not collapse distinct tile sections.
          add(line_id, "transit", id, "transport-line", {index = index, items = U.inventory(line),
            length = line.line_length, collection = "native-owner-line-section"})
        end
      end
    end
    if entity.type == "inserter" then
      add("held:" .. id, "transit", id, "inserter-hand", {items = stack_items(entity.held_stack),
        stack = stack_data(entity.held_stack), position = U.copy(entity.held_stack_position)})
    end
    if entity.type == "mining-drill" then
      local target = entity.mining_target
      add("work:" .. id, "work", id, "native-mining", {progress = entity.mining_progress,
        bonusProgress = entity.bonus_mining_progress, status = entity.status,
        targetId = target and target.valid and U.entity_id(target),
        targetName = target and target.valid and target.name,
        internalOutputBufferObservable = false,
        collection = "native-mining-progress-not-physical-stock"})
    end
    local boxes = entity.fluidbox
    for index = 1, #boxes do
      local segment = boxes.get_fluid_segment_id(index)
      local fluid_id = segment and "fluid:" .. entity.surface.index .. ":segment:" .. segment
        or "fluid:" .. id .. ":box:" .. index
      metadata.fluidStores[#metadata.fluidStores + 1] = fluid_id
      if not by_id[fluid_id] then
        local fluid = boxes[index]
        local contents = segment and boxes.get_fluid_segment_contents(index) or {}
        if not segment and fluid then contents[fluid.name] = fluid.amount end
        U.check(contents ~= nil, "fluid_contents_unavailable", "Native fluid segment contents unavailable")
        add(fluid_id, "fluid", id, segment and "native-segment" or "native-buffer", {
          segmentId = segment, contents = contents, temperature = fluid and fluid.temperature,
          capacity = boxes.get_capacity(index), aggregateSafe = true, sourceBoxes = {}})
      end
      local sources = by_id[fluid_id].data.sourceBoxes
      sources[#sources + 1] = {entityId = id, index = index}
    end
    if entity.type == "assembling-machine" or entity.type == "furnace" or entity.type == "rocket-silo" then
      local recipe = entity.get_recipe()
      local input = entity.get_inventory(entity.type == "furnace" and defines.inventory.furnace_source or defines.inventory.assembling_machine_input)
      local output = entity.get_output_inventory()
      add("work:" .. id, "work", id, "machine-craft", {recipe = recipe and recipe.name,
        inputInventoryId = input and ("inventory:" .. id .. ":" .. input.index),
        outputInventoryId = output and ("inventory:" .. id .. ":" .. output.index),
        inProcess = entity.is_crafting(), progress = entity.crafting_progress, productsFinished = entity.products_finished,
        craftingSpeed = entity.crafting_speed,
        ingredients = recipe and U.copy(recipe.ingredients), products = recipe and U.copy(recipe.products),
        collection = "native-current-process-not-physical-stock"})
    end
  end
  if c then
    add("work:actor", "work", U.entity_id(c), "hand-crafting", {queue = c.crafting_queue or {},
      progress = c.crafting_queue_progress, collection = "native-queue-not-physical-stock"})
    local pilot = s.pilotIndex and game.get_player(s.pilotIndex)
    if pilot and pilot.character == c then
      add("cursor:actor", "inventory", U.entity_id(c), "pilot-cursor", {role = "actor", items = stack_items(pilot.cursor_stack),
        stacks = stack_data(pilot.cursor_stack) and {stack_data(pilot.cursor_stack)} or {}, slots = 1, usableSlots = 1,
        bar = 2, filters = {}, capacityHints = {}})
    end
  end
  table.sort(records, function(a, b) return a.id < b.id end)
  s.factorySnapshotSequence = (s.factorySnapshotSequence or 0) + 1
  return {snapshotId = s.worldId .. ":factory:" .. s.factorySnapshotSequence, scope = Actor.scope(),
    collectedTick = game.tick, expiresTick = game.tick + lifetime, records = records,
    coverage = {atomic = true, knownEntityCount = #ids, knownInventoriesComplete = true,
      knownBeltAndInserterTransitComplete = true, fluidSegmentsDeduplicated = true,
      miningDrillInternalBuffersComplete = false,
      factoryDiscoveryComplete = false, groundItemsComplete = false, reservationsSource = "controller",
      scope = "known-own-entities-actor-and-proven-corpses", capacityItems = requested_capacity}}
end

function M.page(args)
  local s = Actor.state()
  s.factorySnapshots, s.factorySnapshotOrder = s.factorySnapshots or {}, s.factorySnapshotOrder or {}
  local offset = U.number(args.offset, "offset", 0, maximum_records, 0, true)
  local limit = U.number(args.limit, "limit", 1, 200, 100, true)
  local snapshot
  if args.snapshotId then
    U.check(args.capacityItems == nil, "invalid_arguments", "Capacity probes are selected only when creating a snapshot")
    snapshot = s.factorySnapshots[U.string(args.snapshotId, "snapshotId")]
    U.check(snapshot and game.tick <= snapshot.expiresTick, "snapshot_expired", "Snapshot missing, evicted or expired; collect a new one")
  else
    U.check(offset == 0, "invalid_arguments", "A new snapshot must begin at offset zero")
    snapshot = capture(args)
    s.factorySnapshots[snapshot.snapshotId] = snapshot
    s.factorySnapshotOrder[#s.factorySnapshotOrder + 1] = snapshot.snapshotId
    while #s.factorySnapshotOrder > 2 do s.factorySnapshots[table.remove(s.factorySnapshotOrder, 1)] = nil end
  end
  U.check(offset <= #snapshot.records, "invalid_arguments", "Offset exceeds snapshot length")
  local records = {}
  for index = offset + 1, math.min(offset + limit, #snapshot.records) do records[#records + 1] = snapshot.records[index] end
  return {snapshotId = snapshot.snapshotId, scope = Actor.scope(), snapshotScope = snapshot.scope, collectedTick = snapshot.collectedTick,
    expiresTick = snapshot.expiresTick, totalRecords = #snapshot.records, offset = offset,
    nextOffset = offset + #records, complete = offset + #records == #snapshot.records,
    coverage = snapshot.coverage, records = records}
end

return M
