local U = require("scripts.util")
local Actor = require("scripts.actor")
local Operations = require("scripts.operations")
local Visibility = require("scripts.visibility")
local M = {}

local inventory_names = {"fuel", "output", "chest", "input", "lab", "ammo", "rocket", "corpse"}
local Targets = require("scripts.targets")
local function compartment(entity, name, seen)
  local ok, inventory = pcall(Targets.inventory, entity, name)
  if not ok or not inventory or not inventory.valid then return end
  for _, previous in ipairs(seen) do if inventory == previous then return end end
  seen[#seen + 1] = inventory
  return {items = U.inventory(inventory), slots = #inventory, collection = "native-inventory"}
end

function M.entity(entity, include_inventory)
  local result = {id = U.entity_id(entity), name = entity.name, type = entity.type,
    position = U.copy(entity.position), direction = entity.direction, force = entity.force.name,
    collectedTick = game.tick}
  if entity.health then result.health, result.maxHealth = entity.health, entity.max_health end
  if include_inventory then
    result.inventories = {}
    local seen = {}
    for _, name in ipairs(inventory_names) do result.inventories[name] = compartment(entity, name, seen) end
    result.status = entity.status
    if entity.type == "assembling-machine" or entity.type == "furnace" or entity.type == "rocket-silo" then
      local recipe = entity.get_recipe()
      result.recipe, result.craftingProgress = recipe and recipe.name, entity.crafting_progress
    end
    if entity.type == "inserter" then
      local held = entity.held_stack
      if held.valid_for_read then result.heldStack = {name = held.name, count = held.count} end
      result.pickupPosition, result.dropPosition = U.copy(entity.pickup_position), U.copy(entity.drop_position)
    end
    if entity.type == "mining-drill" then result.dropPosition = U.copy(entity.drop_position) end
    -- Fluid boxes are samples, never a summed stock: connected segments can be represented more than once.
    local ok, fluidbox = pcall(function() return entity.fluidbox end)
    if ok and fluidbox and #fluidbox > 0 then
      result.fluidBoxes = {}
      for index = 1, #fluidbox do
        local fluid = fluidbox[index]
        result.fluidBoxes[#result.fluidBoxes + 1] = {index = index, name = fluid and fluid.name,
          amount = fluid and fluid.amount or 0, temperature = fluid and fluid.temperature,
          capacity = fluidbox.get_capacity(index), aggregateSafe = false}
      end
    end
  end
  return result
end

function M.observe(args)
  local s, c = Actor.state(), Actor.get()
  s.observedTargets = s.observedTargets or {}
  for id, record in pairs(s.observedTargets) do
    if not record.entity.valid or game.tick - record.tick > 600 then s.observedTargets[id] = nil end
  end
  local radius = U.number(args.radius, "radius", 1, 64, 32)
  local limit = U.number(args.limit, "limit", 1, 200, 100, true)
  s.snapshotSequence = s.snapshotSequence + 1
  local result = {scope = Actor.scope(), snapshotId = s.snapshotSequence, collectedTick = game.tick,
    agent = {alive = c ~= nil, controlMode = s.controlMode, deaths = s.deaths, respawnTick = s.respawnTick,
      pilotIndex = s.pilotIndex, pilotError = s.pilotError, stopUnconfirmed = s.stopUnconfirmed == true,
      respawnError = s.respawnError},
    operation = Operations.last_receipt(), entities = {}, resources = {}, enemies = {}, players = {},
    coverage = {radius = radius, limit = limit, atomic = true, collectionStartTick = game.tick,
      collectionEndTick = game.tick, factoryComplete = false, transitComplete = false,
      fluidsAggregateSafe = false, enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility", enemyComplete = false},
    goal = {rocketsLaunched = Actor.force() and Actor.force().rockets_launched or 0,
      humanInterventions = s.humanInterventions, fixture = s.fixture == true,
      fixtureReason = s.fixtureReason, fixtureTick = s.fixtureTick}}
  for _, player in pairs(game.players) do
    result.players[#result.players + 1] = {index = player.index, name = player.name, connected = player.connected}
  end
  if not c then return result end
  result.agent.position, result.agent.health = U.copy(c.position), c.health
  result.agent.maxHealth, result.agent.surface = c.max_health, c.surface.name
  result.agent.inventory = U.inventory(c.get_main_inventory())
  result.agent.guns = U.inventory(c.get_inventory(defines.inventory.character_guns))
  result.agent.ammo = U.inventory(c.get_inventory(defines.inventory.character_ammo))
  result.agent.ammoRounds = U.ammo(c.get_inventory(defines.inventory.character_ammo))
  result.agent.craftingQueue = c.crafting_queue
  result.agent.craftingProgress, result.agent.miningProgress = c.crafting_queue_progress, c.character_mining_progress
  result.agent.walking, result.agent.mining = c.walking_state.walking, c.mining_state.mining
  result.agent.reachDistance, result.agent.buildDistance = c.reach_distance, c.build_distance
  local found = c.surface.find_entities_filtered{position = c.position, radius = radius,
    type = {"resource", "tree", "simple-entity", "unit", "unit-spawner", "turret"}, limit = 10000}
  table.sort(found, function(a, b)
    local da, db = U.distance(a.position, c.position), U.distance(b.position, c.position)
    if da ~= db then return da < db end
    return U.entity_id(a) < U.entity_id(b)
  end)
  local resource_count, enemy_count = 0, 0
  for _, entity in ipairs(found) do
    if entity.type == "resource" or entity.type == "tree" or entity.type == "simple-entity" then
      resource_count = resource_count + 1
      if #result.resources < limit then
        local item = M.entity(entity, false)
        if entity.type == "resource" then item.amount = entity.amount end
        result.resources[#result.resources + 1] = item
      end
    elseif entity.force ~= c.force and not c.force.get_friend(entity.force) then
      if Visibility.is_visible(c, entity) then
        enemy_count = enemy_count + 1
        if #result.enemies < limit then
          local observed = M.entity(entity, false)
          result.enemies[#result.enemies + 1] = observed
          s.observedTargets[observed.id] = {entity = entity, tick = game.tick}
        end
      end
    end
  end
  for _, entity in ipairs(c.surface.find_entities_filtered{position = c.position, radius = radius, force = c.force}) do
    if entity ~= c then s.known[U.entity_id(entity)] = entity end
  end
  local ids = {}
  for id, entity in pairs(s.known) do
    if entity.valid and entity.force == c.force then ids[#ids + 1] = id else s.known[id] = nil end
  end
  table.sort(ids)
  for index = 1, math.min(#ids, limit) do result.entities[#result.entities + 1] = M.entity(s.known[ids[index]], true) end
  result.coverage.knownEntityCount, result.coverage.returnedEntityCount = #ids, #result.entities
  result.coverage.knownInventoriesComplete = #ids <= limit
  result.coverage.resourcesTruncated = resource_count > limit or #found == 10000
  result.coverage.enemiesTruncated = enemy_count > limit or #found == 10000
  result.coverage.resourceDiscovery = "local-radius-only"
  result.coverage.factoryRegistry = "agent-built-and-locally-observed-own-entities"
  local research = c.force.current_research
  result.research = {name = research and research.name, progress = c.force.research_progress}
  result.environment = {pollutionEnabled = game.map_settings.pollution.enabled,
    enemyEvolutionEnabled = game.map_settings.enemy_evolution.enabled,
    enemyExpansionEnabled = game.map_settings.enemy_expansion.enabled}
  return result
end

return M
