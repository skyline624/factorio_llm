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

-- The runtime normally returns arrays; a single trigger table is accepted as a one-element list.
local function list(value)
  if value == nil then return {} end
  if value[1] == nil and next(value) ~= nil then return {value} end
  return value
end

-- Native damage of one round of player ammunition: the direct damage effects of its triggers.
function M.round_damage(prototype)
  local ammo_type = prototype.get_ammo_type("player")
  local total = 0
  for _, trigger in ipairs(list(ammo_type and ammo_type.action)) do
    for _, delivery in ipairs(list(trigger.action_delivery)) do
      for _, effect in ipairs(list(delivery.target_effects)) do
        if effect.type == "damage" and effect.damage then total = total + effect.damage.amount end
      end
    end
  end
  return total
end

function M.loadout(character)
  local guns = character.get_inventory(defines.inventory.character_guns)
  local ammo = character.get_inventory(defines.inventory.character_ammo)
  local armor = character.get_inventory(defines.inventory.character_armor)
  local result = {complete = true, slots = {}, carried = {}}
  if armor and #armor > 0 and armor[1].valid_for_read then result.armor = armor[1].name end
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
      if prototype.type == "gun" or prototype.type == "ammo" or prototype.type == "armor" then
        local bullet = prototype.type == "gun" and M.bullet_gun(prototype)
          or prototype.type == "ammo" and prototype.ammo_category.name == "bullet"
        result.carried[#result.carried + 1] = {slot = i, name = stack.name, kind = prototype.type,
          count = stack.count, bullet = bullet, range = prototype.type == "gun" and prototype.attack_parameters.range or 0,
          rounds = prototype.type == "ammo" and ((stack.count - 1) * prototype.magazine_size + stack.ammo) or 0,
          damage = prototype.type == "ammo" and M.round_damage(prototype) or 0}
      end
    end
  end
  return result
end

function M.defense(entity)
  if entity.type ~= "ammo-turret" or not entity.active or entity.health <= 0 or entity.quality.name ~= "normal"
    or entity.prototype.electric_energy_source_prototype then return nil end
  local attack = entity.prototype.attack_parameters
  if not attack or attack.type ~= "projectile" or attack.min_range ~= 0 then return nil end
  local inventory = entity.get_inventory(defines.inventory.turret_ammo)
  if not inventory then return nil end
  for i = 1, #inventory do
    local stack = inventory[i]
    if stack.valid_for_read and stack.quality.name == "normal" and stack.prototype.ammo_category.name == "bullet" then
      local kind = stack.prototype.get_ammo_type("turret")
      return {id = tostring(entity.unit_number), position = {x = entity.position.x, y = entity.position.y},
        range = attack.range * (kind and kind.range_modifier or 1),
        ammoRounds = (stack.count - 1) * stack.prototype.magazine_size + stack.ammo, collectedTick = game.tick}
    end
  end
end

return M
