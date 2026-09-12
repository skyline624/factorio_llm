local U = require("scripts.util")
local Actor = require("scripts.actor")
local M = {}

local function page(items, args)
  local offset = U.number(args.offset, "offset", 0, 100000, 0, true)
  local limit = U.number(args.limit, "limit", 1, 100, 50, true)
  table.sort(items, function(a, b) return a.name < b.name end)
  local result = {}
  for index = offset + 1, math.min(#items, offset + limit) do result[#result + 1] = items[index] end
  return {items = result, total = #items, offset = offset, limit = limit, collectedTick = game.tick,
    complete = offset + limit >= #items}
end

local function matches(name, args)
  if args.name then return name == U.string(args.name, "name") end
  if args.filter then return name:find(U.string(args.filter, "filter"), 1, true) ~= nil end
  return true
end

local function ingredients(items)
  local result = {}
  for _, p in ipairs(items) do
    result[#result + 1] = {name = p.name, type = p.type, amount = p.amount, amountMin = p.amount_min,
      amountMax = p.amount_max, probability = p.probability, temperature = p.temperature,
      minimumTemperature = p.minimum_temperature, maximumTemperature = p.maximum_temperature}
  end
  return result
end

function M.recipes(args)
  local force = Actor.force()
  U.check(force ~= nil, "handshake_required", "Call hello to create the agent first")
  local items = {}
  for name, recipe in pairs(force.recipes) do
    if matches(name, args) and (args.enabledOnly == false or recipe.enabled or args.name) then
      items[#items + 1] = {name = name, enabled = recipe.enabled, category = recipe.category,
        energySeconds = recipe.energy, ingredients = ingredients(recipe.ingredients), products = ingredients(recipe.products),
        hidden = recipe.hidden, handCraftingDisabled = force.get_hand_crafting_disabled_for_recipe(name)}
    end
  end
  return page(items, args)
end

function M.technologies(args)
  local force = Actor.force()
  U.check(force ~= nil, "handshake_required", "Call hello to create the agent first")
  local items = {}
  for name, technology in pairs(force.technologies) do
    local available, prerequisites = technology.enabled and not technology.researched, {}
    for prereq_name, prerequisite in pairs(technology.prerequisites) do
      prerequisites[#prerequisites + 1] = prereq_name
      if not prerequisite.researched then available = false end
    end
    table.sort(prerequisites)
    if matches(name, args) and (args.availableOnly ~= true or available or args.name) then
      items[#items + 1] = {name = name, enabled = technology.enabled, researched = technology.researched,
        available = available, prerequisites = prerequisites, ingredients = ingredients(technology.research_unit_ingredients),
        count = technology.research_unit_count, energyTicks = technology.research_unit_energy,
        trigger = technology.prototype.research_trigger, effects = technology.prototype.effects}
    end
  end
  return page(items, args)
end

return M
