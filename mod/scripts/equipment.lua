local U = require("scripts.util")
local Weapons = require("scripts.weapons")
local M = {}

local function snapshot(c)
  local main, guns, ammo = c.get_main_inventory(), c.get_inventory(defines.inventory.character_guns),
    c.get_inventory(defines.inventory.character_ammo)
  return {main = U.inventory(main), guns = U.inventory(guns), ammo = U.inventory(ammo),
    mainRounds = U.ammo(main), loadedRounds = U.ammo(ammo), selectedSlot = c.selected_gun_index}
end

function M.equip(r, c, args)
  local compartment = U.string(args.compartment, "compartment")
  U.check(compartment == "gun" or compartment == "ammo", "invalid_inventory", "Only gun and ammo slots can be equipped")
  local main = c.get_main_inventory()
  local source_index = U.number(args.sourceSlot, "sourceSlot", 1, #main, nil, true)
  local source = main[source_index]
  local item = U.string(args.item, "item")
  local count = U.number(args.count, "count", 1, compartment == "gun" and 1 or 10, nil, true)
  U.check(source.valid_for_read and source.name == item and source.count >= count and source.quality.name == "normal",
    "equipment_source_changed", "The observed main-inventory stack is no longer available")
  U.check(source.prototype.type == compartment, "invalid_equipment", "The carried item has the wrong equipment type")
  local guns = c.get_inventory(defines.inventory.character_guns)
  local ammo = c.get_inventory(defines.inventory.character_ammo)
  local index = U.number(args.slot, "slot", 1, #guns, nil, true)
  local destination = compartment == "gun" and guns[index] or ammo[index]
  U.check(not destination.valid_for_read, "equipment_slot_changed", "The observed equipment slot is no longer empty")
  if compartment == "gun" then
    U.check(Weapons.bullet_gun(source.prototype) and not ammo[index].valid_for_read,
      "unsupported_weapon", "Only an empty slot for a supported bullet gun can be equipped")
  else
    U.check(guns[index].valid_for_read and Weapons.bullet_gun(guns[index].prototype)
      and source.prototype.ammo_category.name == "bullet", "incompatible_ammo", "The gun cannot use this ammunition")
  end
  U.check(destination.can_set_stack(source), "equipment_capacity", "The native equipment slot cannot accept this stack")
  local before = snapshot(c)
  local initial = source.count
  destination.transfer_stack(source, count)
  local moved = initial - (source.valid_for_read and source.count or 0)
  c.selected_gun_index = index
  local after = snapshot(c)
  r.receipt.effects.compartment, r.receipt.effects.slot = compartment, index
  r.receipt.effects.sourceSlot, r.receipt.effects.item = source_index, item
  r.receipt.effects.requested, r.receipt.effects.transferred = count, moved
  r.receipt.effects.equipmentBefore, r.receipt.effects.equipmentAfter = before, after
  U.check(before.mainRounds + before.loadedRounds == after.mainRounds + after.loadedRounds,
    "equipment_accounting", "Native equipment transfer changed total ammunition rounds")
  U.check(moved > 0, "equipment_transfer_failed", "No carried equipment was transferred")
  return moved == count and "completed" or "partial"
end

function M.select(r, c, args)
  local guns = c.get_inventory(defines.inventory.character_guns)
  local index = U.number(args.slot, "slot", 1, #guns, nil, true)
  U.check(Weapons.slot(c, index).ready, "weapon_not_ready", "The observed weapon is no longer ready")
  r.receipt.effects.previousSlot = c.selected_gun_index
  c.selected_gun_index = index
  r.receipt.effects.selectedSlot = c.selected_gun_index
  return "completed"
end

return M
