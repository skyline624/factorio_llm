local M = {}

function M.observe(character)
  local index = character.selected_gun_index
  local guns = character.get_inventory(defines.inventory.character_guns)
  local ammo = character.get_inventory(defines.inventory.character_ammo)
  local result = {selectedSlot = index, ready = false, rounds = 0}
  if not index or not guns or not ammo then return result end
  local gun, magazine = guns[index], ammo[index]
  if not gun.valid_for_read then return result end
  local attack = gun.prototype.attack_parameters
  result.name = gun.name
  if not attack or not magazine.valid_for_read then return result end
  local ammo_type = magazine.prototype.get_ammo_type("player")
  local category = magazine.prototype.ammo_category
  local compatible = false
  for _, name in ipairs(attack.ammo_categories or {}) do
    if category and name == category.name then compatible = true end
  end
  result.ammunition = magazine.name
  result.rounds = (magazine.count - 1) * magazine.prototype.magazine_size + magazine.ammo
  result.range = attack.range * (ammo_type and ammo_type.range_modifier or 1)
  result.minRange, result.rangeMode = attack.min_range, attack.range_mode
  -- The initial C# reflex supports ordinary bullet weapons. Other weapons need
  -- their own targeting rules before they can be selected automatically.
  result.ready = compatible and category.name == "bullet" and attack.type == "projectile"
    and attack.min_range == 0 and result.rounds > 0
  return result
end

return M
