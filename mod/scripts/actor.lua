local U = require("scripts.util")
local M = {}

-- Values from base/script/freeplay/freeplay.lua in the shipped Factorio 2.0.77.
-- Crash-site salvage is not also created: the initial full kit is the no-intro variant.
local initial_items = { ["iron-plate"] = 8, wood = 1,
  ["burner-mining-drill"] = 1, ["stone-furnace"] = 1 }

function M.initialize()
  storage.agent = storage.agent or {actorId = "character-1", incarnation = 0, generation = 0,
    controlMode = "ai", receipts = {}, receiptOrder = {}, known = {}, snapshotSequence = 0,
    deaths = 0, humanInterventions = 0}
end

function M.state() return storage.agent end

function M.get()
  local entity = storage.agent.character
  if entity and entity.valid then return entity end
end

function M.force()
  return game.forces["factorio_agent"]
end

function M.scope()
  local s = storage.agent
  return {worldId = s.worldId, sessionId = s.sessionId, actorId = s.actorId,
    incarnation = s.incarnation, generation = s.generation}
end

function M.spawn(initial)
  local s = storage.agent
  U.check(not M.get(), "actor_exists", "The actor already exists")
  local surface = game.surfaces.nauvis or game.surfaces[1]
  local force = M.force() or game.create_force("factorio_agent")
  local origin = s.spawnPosition or game.forces.player.get_spawn_position(surface)
  local position = surface.find_non_colliding_position("character", origin, 64, 0.5)
  U.check(position ~= nil, "spawn_blocked", "No collision-free spawn location in generated area")
  local character = surface.create_entity{name = "character", position = position, force = force}
  U.check(character ~= nil, "spawn_failed", "The engine did not create the character")
  character.color = {r = 0.1, g = 0.7, b = 0.95, a = 1}
  character.associated_player = nil
  s.character, s.spawnPosition = character, U.copy(origin)
  s.incarnation, s.generation = s.incarnation + 1, s.generation + 1
  if initial then
    for name, count in pairs(initial_items) do character.insert{name = name, count = count} end
  end
  -- Standard freeplay respawns supply only a pistol and ten magazines, not the initial machines.
  character.get_inventory(defines.inventory.character_guns).insert{name = "pistol", count = 1}
  character.get_inventory(defines.inventory.character_ammo).insert{name = "firearm-magazine", count = 10}
  s.respawnTick = nil
  return character
end

function M.stop(cancel_crafting)
  local c = M.get()
  if not c then return end
  c.walking_state = {walking = false, direction = defines.direction.north}
  c.mining_state = {mining = false}
  c.shooting_state = {state = defines.shooting.not_shooting, position = c.position}
  if cancel_crafting then
    local guard = 0
    while c.crafting_queue_size > 0 and guard < 1000 do
      local entry = c.crafting_queue[1]
      c.cancel_crafting{index = 1, count = entry.count}
      guard = guard + 1
    end
  end
end

function M.on_death(entity)
  local s = storage.agent
  if entity ~= s.character then return false end
  s.lastDeath = {tick = game.tick, position = U.copy(entity.position),
    unitNumber = entity.unit_number, incarnation = s.incarnation, surfaceIndex = entity.surface.index}
  -- The character prototype expresses this duration in seconds, not ticks.
  s.respawnTick = game.tick + entity.prototype.respawn_time * 60
  s.deaths = s.deaths + 1
  s.character = nil
  s.generation = s.generation + 1
  return true
end

return M
