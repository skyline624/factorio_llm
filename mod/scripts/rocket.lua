local U = require("scripts.util")
local Actor = require("scripts.actor")
local M = {}
local statuses = {}
for name, value in pairs(defines.rocket_silo_status) do statuses[value] = name end

function M.observe()
  local c, s = Actor.get(), Actor.state()
  U.check(c ~= nil, "actor_dead", "Rocket observation requires a living actor")
  local result = {scope = Actor.scope(), collectedTick = game.tick, surfaceIndex = c.surface.index,
    rocketsLaunched = c.force.rockets_launched, atomic = true, knownSilosComplete = true, prototypes = {}, silos = {}}
  for item_name, item in pairs(prototypes.item) do
    local prototype = item.place_result
    if prototype and prototype.type == "rocket-silo" then
      result.prototypes[item_name] = {entityName = prototype.name, recipe = prototype.fixed_recipe,
        partsRequired = prototype.rocket_parts_required, craftingSpeed = prototype.get_crafting_speed("normal"),
        energyPerTick = prototype.get_max_energy_usage("normal")}
    end
  end
  for id, entity in pairs(s.known) do
    if entity.valid and entity.force == c.force and entity.surface == c.surface and entity.type == "rocket-silo" then
      U.check(#result.silos < 256, "rocket_observation_budget", "Known silos exceed the atomic observation budget")
      local inventory = entity.get_inventory(defines.inventory.rocket_silo_input)
      local recipe = c.force.recipes[entity.prototype.fixed_recipe]
      local capacity = {}
      for _, ingredient in ipairs(recipe.ingredients) do
        if ingredient.type == "item" then
          capacity[ingredient.name] = inventory.get_insertable_count{name = ingredient.name, quality = "normal"}
        end
      end
      result.silos[#result.silos + 1] = {id = id, name = entity.name, position = U.copy(entity.position),
        recipe = recipe.name, parts = entity.rocket_parts, status = statuses[entity.rocket_silo_status],
        rocketPresent = entity.rocket ~= nil, inProcess = entity.is_crafting(), energy = entity.energy,
        networkId = entity.electric_network_id, inputs = U.inventory(inventory), insertable = capacity}
    end
  end
  table.sort(result.silos, function(a, b) return a.id < b.id end)
  return result
end

return M
