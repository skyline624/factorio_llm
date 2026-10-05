local U = require("scripts.util")
local M = {}

local function held_rounds(character)
  local rounds = 0
  for index = 1, character.get_max_inventory_index() do
    local inventory = character.get_inventory(index)
    if inventory then rounds = rounds + U.ammo(inventory) end
  end
  return rounds
end

function M.prepare(record, character)
  record.work.beforeAllRounds = held_rounds(character)
end

function M.update(record, character)
  if record.work.shotDeathTick then return end
  local before, surviving = record.work.beforeAllRounds, held_rounds(character)
  local known = before ~= nil and surviving <= before
  record.receipt.effects.roundsConsumed = known and (before - surviving) or nil
  record.receipt.effects.ammoAccounting = {status = known and "observed-native-actor" or "unresolved-native-actor",
    collectedTick = game.tick, beforeRounds = before, survivingRounds = surviving,
    collection = "actor-all-ammunition-rounds"}
end

function M.on_death(record)
  if not record.work or record.work.beforeAllRounds == nil then return end
  local effects = record.receipt.effects
  record.work.shotDeathTick = game.tick
  effects.ammoAccounting = {status = "awaiting-native-corpse", collectedTick = game.tick,
    beforeRounds = record.work.beforeAllRounds,
    lastObservedRoundsConsumed = effects.roundsConsumed}
  -- The empty dead-character inventory is a transfer, not evidence of fired rounds.
  effects.roundsConsumed, effects.ammoInventoryDelta = nil, nil
end

function M.reconcile(record, corpses, tick)
  if not record or not record.work or record.work.shotDeathTick ~= tick then return end
  local before, surviving, ids = record.work.beforeAllRounds, 0, {}
  for id, corpse in pairs(corpses) do
    surviving = surviving + U.ammo(corpse.get_inventory(defines.inventory.character_corpse))
    ids[#ids + 1] = id
  end
  table.sort(ids)
  local effects = record.receipt.effects
  if #ids == 0 or before == nil or surviving > before then
    effects.ammoAccounting.status = "unresolved-native-corpse"
    return
  end
  effects.roundsConsumed = before - surviving
  effects.ammoAccounting = {status = "reconciled-native-corpse", collectedTick = tick,
    beforeRounds = before, survivingRounds = surviving, corpseIds = ids,
    collection = "actor-all-ammunition-rounds-to-proven-native-corpses"}
end

return M
