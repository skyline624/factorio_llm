local M = {}

function M.slot(character, index)
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

function M.observe(character) return M.slot(character, character.selected_gun_index) end

function M.bullet_gun(prototype)
  local attack = prototype.attack_parameters
  if not attack or attack.type ~= "projectile" or attack.min_range ~= 0 then return false end
  for _, name in ipairs(attack.ammo_categories or {}) do if name == "bullet" then return true end end
  return false
end

function M.loadout(character)
  local guns = character.get_inventory(defines.inventory.character_guns)
  local ammo = character.get_inventory(defines.inventory.character_ammo)
  local result = {complete = true, slots = {}, carried = {}}
  for i = 1, #guns do
    local gun, magazine, state = guns[i], ammo[i], M.slot(character, i)
    result.slots[#result.slots + 1] = {index = i, gun = gun.valid_for_read and gun.name or nil,
      bulletGun = gun.valid_for_read and M.bullet_gun(gun.prototype) or false,
      range = gun.valid_for_read and gun.prototype.attack_parameters.range or 0,
      ammo = magazine.valid_for_read and magazine.name or nil, rounds = state.rounds, ready = state.ready}
  end
  local main = character.get_main_inventory()
  for i = 1, #main do
    local stack = main[i]
    if stack.valid_for_read and stack.quality.name == "normal" then
      local prototype = stack.prototype
      if prototype.type == "gun" or prototype.type == "ammo" then
        local bullet = prototype.type == "gun" and M.bullet_gun(prototype)
          or prototype.type == "ammo" and prototype.ammo_category.name == "bullet"
        result.carried[#result.carried + 1] = {slot = i, name = stack.name, kind = prototype.type,
          count = stack.count, bullet = bullet, range = prototype.type == "gun" and prototype.attack_parameters.range or 0,
          rounds = prototype.type == "ammo" and ((stack.count - 1) * prototype.magazine_size + stack.ammo) or 0}
      end
    end
  end
  return result
end

return M
