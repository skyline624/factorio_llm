local U = require("scripts.util")
local M = {}

local function equipment(c)
  return {c.get_inventory(defines.inventory.character_guns), c.get_inventory(defines.inventory.character_ammo),
    c.get_inventory(defines.inventory.character_armor)}
end

local function count(inv, name) return inv.get_item_count{name = name, quality = "normal"} end

local function sample(c, expected)
  local result = {main = {}, equipment = {}, slots = {}, mainRounds = U.ammo(c.get_main_inventory()), equipmentRounds = 0}
  for name in pairs(expected) do result.main[name] = count(c.get_main_inventory(), name) end
  for _, inv in ipairs(equipment(c)) do
    local values = {}
    for name in pairs(expected) do values[name] = count(inv, name) end
    result.equipment[tostring(inv.index)] = values
    local slots = {}
    for i = 1, #inv do
      local stack = inv[i]
      if stack.valid_for_read then slots[tostring(i)] = {name = stack.name, quality = stack.quality.name, count = stack.count} end
    end
    result.slots[tostring(inv.index)] = slots
    result.equipmentRounds = result.equipmentRounds + U.ammo(inv)
  end
  return result
end

function M.prepare(c, expected, requested)
  local count = requested
  for name, amount in pairs(expected) do
    count = math.min(count, math.floor(c.get_main_inventory().get_insertable_count{name = name, quality = "normal"} / amount))
    local prototype = prototypes.item[name]
    if prototype.type == "ammo" then
      local partial_equipped = false
      for _, inv in ipairs(equipment(c)) do
        for i = 1, #inv do
          local stack = inv[i]
          if stack.valid_for_read and stack.name == name and stack.quality.name == "normal" and stack.ammo < prototype.magazine_size then
            partial_equipped = true
          end
        end
      end
      if partial_equipped then
        local separate_capacity = false
        local main = c.get_main_inventory()
        for i = 1, #main do
          local stack = main[i]
          if not stack.valid_for_read or stack.name == name and stack.quality.name == "normal"
            and stack.ammo == prototype.magazine_size and stack.count < prototype.stack_size then separate_capacity = true; break end
        end
        U.check(separate_capacity, "craft_output_capacity", "A partial equipped magazine needs capacity without merging partial stacks")
      end
    end
  end
  -- Conservative current capacity, without assuming ingredient consumption will free slots.
  U.check(count > 0, "craft_output_capacity", "No current main-inventory capacity for one craft output")
  return {before = sample(c, expected), transferred = {}}, count
end

local function deliver(record, c)
  local w = record.work
  if not w or not w.delivery or not w.queued then return end
  local delivery, main = w.delivery, c.get_main_inventory()
  for _, inv in ipairs(equipment(c)) do
    local baseline = delivery.before.equipment[tostring(inv.index)]
    local slots = delivery.before.slots[tostring(inv.index)]
    for name in pairs(w.expected) do
      local excess = count(inv, name) - baseline[name]
      U.check(excess >= 0, "craft_equipment_changed", "Preexisting product equipment was removed during crafting")
      for i = 1, #inv do
        local source = inv[i]
        local previous = slots[tostring(i)]
        local reserved = previous and previous.name == name and previous.quality == "normal" and previous.count or 0
        if source.valid_for_read and source.name == name and source.quality.name == "normal" then
          for j = 1, #main do
            if excess <= 0 or not source.valid_for_read or source.count <= reserved then break end
            local target = main[j]
            local compatible = not target.valid_for_read or target.name == name and target.quality.name == "normal"
            -- Combining two partial magazines can reduce item counts. Preserve both native stacks instead.
            local merges_partial = compatible and target.valid_for_read and source.prototype.type == "ammo"
              and source.ammo < source.prototype.magazine_size and target.ammo < target.prototype.magazine_size
            if compatible and not merges_partial and main.can_insert(source) then
              local before_source, before_main = source.count, count(main, name)
              local rounds = U.ammo(main) + U.ammo(inv)
              target.transfer_stack(source, math.min(excess, before_source - reserved))
              local moved = before_source - (source.valid_for_read and source.count or 0)
              delivery.transferred[name] = (delivery.transferred[name] or 0) + moved
              excess = excess - moved
              U.check(count(main, name) - before_main == moved and U.ammo(main) + U.ammo(inv) == rounds,
                "craft_delivery_accounting", "Native craft delivery changed item or ammunition totals")
            end
          end
        end
      end
      U.check(excess == 0, "craft_delivery_blocked", "Newly equipped craft outputs could not be delivered to the main inventory")
    end
  end
end

function M.update(record, c)
  local w = record.work
  if not w or not w.delivery or not w.queued then return end
  local ok, err = pcall(deliver, record, c)
  record.receipt.effects.craftDelivery = {source = "native-equipment-excess-transfer", before = w.delivery.before,
    after = sample(c, w.expected), transferred = U.copy(w.delivery.transferred)}
  if not ok then error(err, 0) end
end

return M
