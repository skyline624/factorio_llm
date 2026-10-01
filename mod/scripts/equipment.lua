local U = require("scripts.util")
local Weapons = require("scripts.weapons")
local M = {}

local function snapshot(c)
  local main, guns, ammo = c.get_main_inventory(), c.get_inventory(defines.inventory.character_guns),
    c.get_inventory(defines.inventory.character_ammo)
  return {main = U.inventory(main), guns = U.inventory(guns), ammo = U.inventory(ammo),
    armor = U.inventory(c.get_inventory(defines.inventory.character_armor)),
    mainRounds = U.ammo(main), loadedRounds = U.ammo(ammo), selectedSlot = c.selected_gun_index}
end

local function receipt(r, compartment, index, source_index, item, count, moved, before, after)
  r.receipt.effects.compartment, r.receipt.effects.slot = compartment, index
  r.receipt.effects.sourceSlot, r.receipt.effects.item = source_index, item
  r.receipt.effects.requested, r.receipt.effects.transferred = count, moved
  r.receipt.effects.equipmentBefore, r.receipt.effects.equipmentAfter = before, after
end

-- Wears a carried armor. A worn armor is swapped back into the source slot, never destroyed, and only when the main
-- inventory cannot shrink: removing an inventory bonus could spill carried items.
local function wear(r, c, args, source, source_index, item)
  local armor = c.get_inventory(defines.inventory.character_armor)
  U.check(armor ~= nil and #armor > 0, "invalid_inventory", "The character has no armor slot")
  local index = U.number(args.slot, "slot", 1, #armor, nil, true)
  local destination = armor[index]
  U.check(args.replaces == nil or type(args.replaces) == "string", "invalid_arguments", "replaces must name the worn armor")
  local worn = destination.valid_for_read and destination.name or nil
  U.check(worn == args.replaces, "equipment_slot_changed", "The observed armor slot changed")
  local before = snapshot(c)
  if worn then
    U.check(source.prototype.get_inventory_size_bonus("normal") >= destination.prototype.get_inventory_size_bonus("normal"),
      "equipment_inventory_shrink", "Swapping would shrink the main inventory")
    U.check(destination.swap_stack(source), "equipment_transfer_failed", "The native armor swap failed")
  else
    U.check(destination.can_set_stack(source), "equipment_capacity", "The native armor slot cannot accept this stack")
    destination.transfer_stack(source, 1)
  end
  local after = snapshot(c)
  local moved = (after.armor[item] or 0) - (before.armor[item] or 0)
  receipt(r, "armor", index, source_index, item, 1, moved, before, after)
  r.receipt.effects.replaced = worn
  U.check(moved == 1, "equipment_transfer_failed", "The carried armor was not worn")
  return "completed"
end

function M.equip(r, c, args)
  local compartment = U.string(args.compartment, "compartment")
  U.check(compartment == "gun" or compartment == "ammo" or compartment == "armor", "invalid_inventory",
    "Only gun, ammo and armor slots can be equipped")
  local main = c.get_main_inventory()
  local source_index = U.number(args.sourceSlot, "sourceSlot", 1, #main, nil, true)
  local source = main[source_index]
  local item = U.string(args.item, "item")
  local count = U.number(args.count, "count", 1, compartment == "ammo" and 10 or 1, nil, true)
  U.check(source.valid_for_read and source.name == item and source.count >= count and source.quality.name == "normal",
    "equipment_source_changed", "The observed main-inventory stack is no longer available")
  U.check(source.prototype.type == compartment, "invalid_equipment", "The carried item has the wrong equipment type")
  if compartment == "armor" then return wear(r, c, args, source, source_index, item) end
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
  receipt(r, compartment, index, source_index, item, count, moved, before, after)
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
