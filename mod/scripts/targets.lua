local U = require("scripts.util")
local Recovery = require("scripts.recovery")
local M = {}

function M.find(character, args)
  local target
  if args.entityId then
    local reference = U.string(args.entityId, "entityId")
    -- Many base buildings do not set get-by-unit-number, even though they have a
    -- unit_number. Retain the native references registered during build/observation.
    target = storage.agent and storage.agent.known[reference]
    if not target then target = Recovery.find(reference) end
    if not target and storage.agent and storage.agent.observedTargets then
      local observed = storage.agent.observedTargets[reference]
      if observed then target = observed.entity end
    end
    if target and not target.valid then target = nil end
    local id = tonumber(reference)
    if not target and id then target = game.get_entity_by_unit_number(id) end
    U.check(target and target.valid and target.surface == character.surface,
      "target_missing", "The requested entity no longer exists on this surface")
  else
    local position = U.position(args.position)
    local filter = {position = position, radius = 0.6}
    if args.name then filter.name = U.string(args.name, "name") end
    local candidates = character.surface.find_entities_filtered(filter)
    table.sort(candidates, function(a, b)
      local da, db = U.distance(a.position, position), U.distance(b.position, position)
      if da ~= db then return da < db end
      return U.entity_id(a) < U.entity_id(b)
    end)
    for _, candidate in ipairs(candidates) do
      if candidate ~= character and candidate.type ~= "character" then target = candidate; break end
    end
    U.check(target ~= nil, "target_missing", "No matching entity at the requested position")
  end
  U.check(target ~= character, "invalid_target", "The actor cannot target itself")
  return target
end

function M.reachable(character, target)
  U.check(character.can_reach_entity(target), "out_of_reach", "Move into the native interaction reach first")
end

function M.owned(character, target)
  U.check(target.force == character.force or Recovery.id(target) ~= nil, "wrong_force",
    "Only the agent's own entities or proven character corpses may be changed")
end

function M.id(entity) return Recovery.id(entity) or U.entity_id(entity) end

function M.inventory(entity, slot)
  if slot == "fuel" then return entity.get_fuel_inventory() end
  if slot == "output" then return entity.get_output_inventory() end
  local types = {chest = {container = true, ["logistic-container"] = true}, lab = {lab = true},
    ammo = {["ammo-turret"] = true}, rocket = {["rocket-silo"] = true},
    corpse = {["character-corpse"] = true}}
  local indices = {chest = defines.inventory.chest, lab = defines.inventory.lab_input,
    ammo = defines.inventory.turret_ammo, rocket = defines.inventory.rocket_silo_rocket,
    corpse = defines.inventory.character_corpse}
  if slot == "input" then
    if entity.type == "furnace" then return entity.get_inventory(defines.inventory.furnace_source) end
    if entity.type == "lab" then return entity.get_inventory(defines.inventory.lab_input) end
    if entity.type == "rocket-silo" then return entity.get_inventory(defines.inventory.rocket_silo_input) end
    if entity.type == "assembling-machine" then return entity.get_inventory(defines.inventory.assembling_machine_input) end
    return nil
  end
  U.check(indices[slot] ~= nil, "invalid_inventory", "Unknown inventory compartment")
  if not types[slot][entity.type] then return nil end
  return entity.get_inventory(indices[slot])
end

return M
