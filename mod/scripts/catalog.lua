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

function M.production()
  local c = Actor.get()
  U.check(c ~= nil, "actor_dead", "Production catalog requires a living character")
  local result = {scope = Actor.scope(), collectedTick = game.tick, recipes = {}, items = {}, mining = {}, machines = {}, assemblers = {},
    handCategories = c.prototype.crafting_categories}
  for name, recipe in pairs(c.force.recipes) do
    if not recipe.hidden then
      result.recipes[#result.recipes + 1] = {name = name, enabled = recipe.enabled, category = recipe.category,
        energySeconds = recipe.energy, ingredients = ingredients(recipe.ingredients), products = ingredients(recipe.products),
        handCraftingDisabled = c.force.get_hand_crafting_disabled_for_recipe(name)}
    end
  end
  table.sort(result.recipes, function(a, b) return a.name < b.name end)
  for name, item in pairs(prototypes.item) do
    result.items[name] = {fuelValue = item.fuel_value, fuelCategory = item.fuel_category,
      placeEntity = item.place_result and item.place_result.name, placeEntityType = item.place_result and item.place_result.type,
      stackSize = item.stack_size}
    local entity = item.place_result
    if entity and entity.type == "furnace" and entity.burner_prototype then
      result.machines[name] = {entityName = entity.name, categories = entity.crafting_categories,
        fuelCategories = entity.burner_prototype.fuel_categories, craftingSpeed = entity.get_crafting_speed("normal")}
    end
    if entity and entity.type == "assembling-machine" and entity.electric_energy_source_prototype then
      local fluid_inputs, fluid_outputs = 0, 0
      for _, box in ipairs(entity.fluidbox_prototypes) do
        if box.production_type == "input" or box.production_type == "input-output" then fluid_inputs = fluid_inputs + 1 end
        if box.production_type == "output" or box.production_type == "input-output" then fluid_outputs = fluid_outputs + 1 end
      end
      result.assemblers[name] = {entityName = entity.name, categories = entity.crafting_categories,
        craftingSpeed = entity.get_crafting_speed("normal"), energyPerTick = entity.get_max_energy_usage("normal"),
        ingredientCount = entity.ingredient_count, fixedRecipe = entity.fixed_recipe,
        fluidInputCount = fluid_inputs, fluidOutputCount = fluid_outputs}
    end
  end
  for name, entity in pairs(prototypes.entity) do
    if entity.type == "resource" or entity.type == "tree" then
      local properties = entity.mineable_properties
      if properties and properties.minable and not properties.required_fluid and #(properties.products or {}) > 0 then
        result.mining[name] = ingredients(properties.products or {})
      end
    end
  end
  return result
end

return M
