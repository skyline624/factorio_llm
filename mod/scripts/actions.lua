local U = require("scripts.util")
local T = require("scripts.targets")
local Actor = require("scripts.actor")
local M = {}

M.capabilities = {"move", "mine", "craft", "wait", "build", "insert", "take", "set_recipe",
  "research", "rotate", "shoot", "launch_rocket"}
local starts, steps = {}, {}

local function main_inventory(c) return c.get_main_inventory() end
local function target(c, args, owned)
  local entity = T.find(c, args)
  T.reachable(c, entity)
  if owned then T.owned(c, entity) end
  return entity
end

function M.measure(record, c)
  local w, effects = record.work, record.receipt.effects
  if not w or not c or not c.valid then return end
  effects.position = U.copy(c.position)
  effects.inventoryDelta = U.delta(w.beforeInventory, U.inventory(main_inventory(c)))
  effects.collection = "actor-main-inventory-delta-exclusive-operation"
  effects.elapsedTicks = game.tick - record.receipt.acceptedTick
  if w.product then
    effects.produced = math.max(0, main_inventory(c).get_item_count(w.product) - w.beforeProduct)
  end
  if w.expected then
    effects.products = {}
    for name in pairs(w.expected) do
      effects.products[name] = math.max(0, main_inventory(c).get_item_count(name) - (w.beforeInventory[name] or 0))
    end
  end
  if w.beforeRounds then
    effects.roundsConsumed = w.beforeRounds - U.ammo(c.get_inventory(defines.inventory.character_ammo))
  end
end

function M.start(record, c)
  local request = record.request
  U.check(starts[request.kind] ~= nil, "unknown_kind", "Unsupported operation kind: " .. request.kind)
  record.work = {beforeInventory = U.inventory(main_inventory(c)),
    startPosition = U.copy(c.position), lastProgressTick = game.tick}
  return starts[request.kind](record, c, request.args)
end

function M.step(record, c)
  local step = steps[record.request.kind]
  if step then return step(record, c, record.request.args) end
  return "completed"
end

starts.move = function(r, c, args)
  r.work.destination = U.position(args.position)
  r.work.tolerance = U.number(args.tolerance, "tolerance", 0.15, 2, 0.3)
  U.check(U.distance(c.position, r.work.destination) <= 256, "move_too_far", "A native move is limited to 256 tiles")
  r.work.lastPosition = U.copy(c.position)
end

steps.move = function(r, c)
  local w = r.work
  if U.distance(c.position, w.destination) <= w.tolerance then return "completed" end
  if U.distance(c.position, w.lastPosition) > 0.01 then
    w.lastProgressTick, w.lastPosition = game.tick, U.copy(c.position)
  end
  U.check(game.tick - w.lastProgressTick < 180, "path_blocked", "No movement for 180 ticks; request a new C# route")
  local dx, dy = w.destination.x - c.position.x, w.destination.y - c.position.y
  local angle = math.atan2(dx, -dy)
  local direction = (math.floor(angle / (math.pi / 4) + 0.5) % 8) * 2
  c.walking_state = {walking = true, direction = direction}
end

starts.wait = function(r, _, args)
  r.work.endTick = game.tick + U.number(args.ticks, "ticks", 1, 216000, nil, true)
end
steps.wait = function(r) if game.tick >= r.work.endTick then return "completed" end end

starts.mine = function(r, c, args)
  local entity = target(c, args, false)
  U.check(entity.minable, "not_minable", "The entity is not minable")
  U.check(entity.force == c.force or entity.force.name == "neutral", "wrong_force", "Cannot mine another force's entity")
  local properties = entity.prototype.mineable_properties
  U.check(properties and properties.minable, "not_minable", "The prototype cannot be mined")
  local product
  for _, p in ipairs(properties.products or {}) do if p.type == "item" then product = p.name; break end end
  U.check(product ~= nil, "unsupported_mining", "Only mining that yields a solid item is supported")
  r.work.target, r.work.product = entity, product
  r.work.requested = U.number(args.count, "count", 1, 1000, 1, true)
  r.work.beforeProduct = main_inventory(c).get_item_count(product)
  r.work.previousProduct = r.work.beforeProduct
  r.receipt.effects.targetId = U.entity_id(entity)
  r.receipt.effects.product = product
  r.receipt.effects.requested = r.work.requested
  c.selected = entity
  U.check(c.selected == entity, "not_selectable", "The native character cannot select this entity")
end

steps.mine = function(r, c)
  local w, effects = r.work, r.receipt.effects
  local current = main_inventory(c).get_item_count(w.product)
  effects.produced = math.max(0, current - w.beforeProduct)
  if current ~= w.previousProduct then w.lastProgressTick, w.previousProduct = game.tick, current end
  if effects.produced >= w.requested then return "completed" end
  if not w.target.valid then
    if effects.produced > 0 then return "partial" end
    U.fail("target_lost", "Mining target disappeared without an observed product")
  end
  T.reachable(c, w.target)
  U.check(main_inventory(c).can_insert{name = w.product, count = 1}, "inventory_full", "No capacity for mining product")
  U.check(game.tick - w.lastProgressTick <= 3600, "mining_stalled", "No mining product observed for 3600 ticks")
  c.selected = w.target
  c.mining_state = {mining = true, position = w.target.position}
  effects.nativeProgress = c.character_mining_progress
end

starts.craft = function(r, c, args)
  local name = U.string(args.recipe, "recipe")
  local recipe = c.force.recipes[name]
  U.check(recipe and recipe.enabled, "recipe_locked", "Recipe is unavailable or disabled")
  U.check(c.crafting_queue_size == 0, "crafting_busy", "The native crafting queue is not empty")
  local requested = U.number(args.count, "count", 1, 1000, 1, true)
  r.work.expected = {}
  for _, product in ipairs(recipe.products) do
    U.check(product.type == "item" and product.amount and (not product.probability or product.probability == 1),
      "unsupported_craft", "Only deterministic solid hand-crafting products are supported")
    r.work.expected[product.name] = (r.work.expected[product.name] or 0) + product.amount
  end
  local queued = c.begin_crafting{recipe = name, count = requested, silent = true}
  r.work.requested, r.work.queued = requested, queued
  r.receipt.effects.requested, r.receipt.effects.queued = requested, queued
  r.receipt.effects.recipe = name
  U.check(queued > 0, "cannot_craft", "Native crafting rejected the recipe or its ingredients")
end

steps.craft = function(r, c)
  local w, effects = r.work, r.receipt.effects
  effects.nativeQueueSize, effects.nativeProgress = c.crafting_queue_size, c.crafting_queue_progress
  local products = {}
  for name in pairs(w.expected) do
    products[name] = math.max(0, main_inventory(c).get_item_count(name) - (w.beforeInventory[name] or 0))
  end
  effects.products = products
  if c.crafting_queue_size > 0 then return end
  for name, amount in pairs(w.expected) do
    U.check(products[name] >= amount * w.queued, "craft_output_unconfirmed",
      "Queue ended but the required output was not observed in the actor inventory")
  end
  return w.queued == w.requested and "completed" or "partial"
end

starts.build = function(r, c, args)
  local item = U.string(args.item or args.name, "item")
  local prototype = prototypes.item[item]
  U.check(prototype and prototype.place_result, "not_buildable", "The item does not place a supported entity")
  local name, position = prototype.place_result.name, U.position(args.position)
  local direction = U.number(args.direction, "direction", 0, 15, 0, true)
  U.check(direction % 4 == 0, "invalid_direction", "Base buildings use cardinal directions 0,4,8,12")
  U.check(U.distance(c.position, position) <= c.build_distance, "out_of_reach", "Build location is outside character reach")
  local stack = main_inventory(c).find_item_stack{name = item, quality = "normal"}
  U.check(stack ~= nil, "missing_item", "The character does not have the construction item")
  U.check(c.can_place_entity{name = name, position = position, direction = direction}
    and c.surface.can_place_entity{name = name, position = position, direction = direction,
      force = c.force, build_check_type = defines.build_check_type.manual},
    "placement_blocked", "Native placement validation failed")
  local entity = c.surface.create_entity{name = name, position = position, direction = direction,
    force = c.force, item = stack, raise_built = true, create_build_effect_smoke = true}
  U.check(entity and entity.valid, "build_failed", "Engine did not create the entity")
  stack.count = stack.count - 1
  Actor.state().known[U.entity_id(entity)] = entity
  r.receipt.effects.entityId, r.receipt.effects.entityName = U.entity_id(entity), entity.name
  r.receipt.effects.consumed = {[item] = 1}
  r.receipt.effects.entityPosition = U.copy(entity.position)
  return "completed"
end

local function transfer(r, c, args, taking)
  local entity = target(c, args, true)
  local item = U.string(args.item, "item")
  U.check(prototypes.item[item] ~= nil, "unknown_item", "Unknown solid item")
  local requested = U.number(args.count, "count", 1, 100000, nil, true)
  local slot = U.string(args.inventory, "inventory", taking and "output" or "input")
  local other = T.inventory(entity, slot)
  U.check(other and other.valid, "invalid_inventory", "The entity has no such inventory")
  local source, destination = main_inventory(c), other
  if taking then source, destination = other, main_inventory(c) end
  local moved = 0
  local max_slot = #destination
  if destination.supports_bar() then max_slot = math.min(max_slot, destination.get_bar() - 1) end
  for i = 1, #source do
    local stack = source[i]
    if stack.valid_for_read and stack.name == item and stack.quality.name == "normal" then
      for j = 1, max_slot do
        if moved >= requested or not stack.valid_for_read then break end
        if destination.can_insert(stack) then
          local before = stack.count
          destination[j].transfer_stack(stack, math.min(requested - moved, before))
          local after = stack.valid_for_read and stack.count or 0
          moved = moved + before - after
        end
      end
    end
    if moved >= requested then break end
  end
  r.receipt.effects.targetId, r.receipt.effects.inventory = U.entity_id(entity), slot
  r.receipt.effects.item, r.receipt.effects.requested, r.receipt.effects.transferred = item, requested, moved
  r.receipt.effects.direction = taking and "to_actor" or "from_actor"
  U.check(moved > 0, "transfer_blocked", "No matching stock or no compatible destination capacity")
  return moved == requested and "completed" or "partial"
end
starts.insert = function(r, c, args) return transfer(r, c, args, false) end
starts.take = function(r, c, args) return transfer(r, c, args, true) end

starts.set_recipe = function(r, c, args)
  local entity = target(c, args, true)
  U.check(entity.type == "assembling-machine", "unsupported_entity", "Only assembling machines accept explicit recipes")
  local name = U.string(args.recipe, "recipe")
  U.check(c.force.recipes[name] and c.force.recipes[name].enabled, "recipe_locked", "Recipe is not unlocked")
  local previous = entity.get_recipe()
  r.receipt.effects.previousRecipe = previous and previous.name
  local displaced = entity.set_recipe(name)
  local returned, spilled = {}, {}
  for _, stack in ipairs(displaced) do
    local inserted = main_inventory(c).insert(stack)
    returned[stack.name] = (returned[stack.name] or 0) + inserted
    if inserted < stack.count then
      stack.count = stack.count - inserted
      c.surface.spill_item_stack{position = c.position, stack = stack, enable_looted = true, allow_belts = false}
      spilled[stack.name] = (spilled[stack.name] or 0) + stack.count
    end
  end
  r.receipt.effects.returned, r.receipt.effects.spilled = returned, spilled
  local actual = entity.get_recipe()
  U.check(actual and actual.name == name, "recipe_rejected", "The machine did not accept the recipe")
  r.receipt.effects.recipe, r.receipt.effects.targetId = name, U.entity_id(entity)
  return "completed"
end

starts.research = function(r, c, args)
  local name = U.string(args.technology, "technology")
  local tech = c.force.technologies[name]
  U.check(tech and tech.enabled and not tech.researched, "technology_unavailable", "Technology unavailable or already researched")
  U.check(tech.prototype.research_trigger == nil, "trigger_technology", "This technology requires a native gameplay trigger")
  for _, prerequisite in pairs(tech.prerequisites) do
    U.check(prerequisite.researched, "prerequisite_missing", "Missing research: " .. prerequisite.name)
  end
  U.check(c.force.add_research(name), "research_rejected", "Engine rejected research selection")
  r.receipt.effects.technology, r.receipt.effects.queued = name, true
  r.receipt.effects.researched = tech.researched
  return "completed" -- The operation selects research; labs must still perform it.
end

starts.rotate = function(r, c, args)
  local entity = target(c, args, true)
  local before = entity.direction
  U.check(entity.rotate{reverse = args.reverse == true}, "rotation_rejected", "Engine refused rotation")
  r.receipt.effects.targetId = U.entity_id(entity)
  r.receipt.effects.beforeDirection, r.receipt.effects.afterDirection = before, entity.direction
  return "completed"
end

starts.shoot = function(r, c, args)
  local entity = T.find(c, args)
  U.check(entity.force ~= c.force and not c.force.get_friend(entity.force), "not_enemy", "Target is not hostile")
  local chunk = {x = math.floor(entity.position.x / 32), y = math.floor(entity.position.y / 32)}
  U.check(c.force.is_chunk_visible(entity.surface, chunk), "target_not_visible", "Enemy is outside currently visible chunks")
  r.work.target = entity
  r.work.endTick = game.tick + U.number(args.ticks, "ticks", 1, 3600, 60, true)
  r.work.beforeAmmo = U.inventory(c.get_inventory(defines.inventory.character_ammo))
  r.work.beforeRounds = U.ammo(c.get_inventory(defines.inventory.character_ammo))
  U.check(r.work.beforeRounds > 0, "no_ammo", "No loaded ammunition is available")
  r.receipt.effects.targetId = U.entity_id(entity)
  r.receipt.effects.beforeHealth = entity.health
end

steps.shoot = function(r, c)
  local entity = r.work.target
  r.receipt.effects.ammoInventoryDelta = U.delta(r.work.beforeAmmo,
    U.inventory(c.get_inventory(defines.inventory.character_ammo)))
  r.receipt.effects.roundsConsumed = r.work.beforeRounds - U.ammo(c.get_inventory(defines.inventory.character_ammo))
  if not entity.valid then r.receipt.effects.targetGone = true; return "completed" end
  local chunk = {x = math.floor(entity.position.x / 32), y = math.floor(entity.position.y / 32)}
  U.check(c.force.is_chunk_visible(entity.surface, chunk), "target_not_visible", "Lost current target visibility")
  r.receipt.effects.afterHealth = entity.health
  if game.tick >= r.work.endTick then
    U.check(r.receipt.effects.roundsConsumed > 0, "no_shots_observed", "No ammunition consumption was observed")
    return "completed"
  end
  c.shooting_state = {state = defines.shooting.shooting_enemies, position = entity.position}
end

starts.launch_rocket = function(r, c, args)
  local entity = target(c, args, true)
  U.check(entity.type == "rocket-silo", "not_silo", "Target must be a rocket silo")
  r.work.rocketsBefore = c.force.rockets_launched
  U.check(entity.launch_rocket(), "rocket_not_ready", "The native silo is not ready for launch")
  r.receipt.effects.targetId = U.entity_id(entity)
  r.receipt.effects.launchOrdered = true
end
steps.launch_rocket = function(r, c)
  r.receipt.effects.rocketsLaunched = c.force.rockets_launched - r.work.rocketsBefore
  if r.receipt.effects.rocketsLaunched > 0 then return "completed" end
end

return M
