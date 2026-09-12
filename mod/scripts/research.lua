local U = require("scripts.util")
local Actor = require("scripts.actor")
local M = {}

local function science_units(inventory)
  local units = {}
  for index = 1, #inventory do
    local stack = inventory[index]
    if stack.valid_for_read and stack.prototype.type == "tool" then
      U.check(stack.quality.name == "normal", "unsupported_science_item", "Only normal-quality science packs are supported")
      local durability = stack.prototype.get_durability(stack.quality)
      U.check(durability and durability > 0, "invalid_science_durability", "Science durability unavailable")
      units[stack.name] = (units[stack.name] or 0) + stack.count - 1 + stack.durability / durability
    end
  end
  return units
end

function M.observe(args)
  local c, s = Actor.get(), Actor.state()
  U.check(c ~= nil, "actor_dead", "Research observation requires the living actor")
  local name = U.string(args.technology, "technology")
  local technology = c.force.technologies[name]
  U.check(technology ~= nil, "technology_missing", "Unknown native technology")
  local current = c.force.current_research
  local result = {scope = Actor.scope(), collectedTick = game.tick, surfaceIndex = c.surface.index,
    technology = name, researched = technology.researched, selected = current and current.name,
    progress = current == technology and c.force.research_progress or technology.saved_progress,
    prototypes = {}, labs = {}, knownLabsComplete = true, atomic = true, consumed = {},
    actorItems = U.inventory(c.get_main_inventory()), actorScienceUnits = science_units(c.get_main_inventory())}
  for item_name, item in pairs(prototypes.item) do
    local entity = item.place_result
    if entity and entity.type == "lab" then
      result.prototypes[item_name] = {entityName = entity.name, inputs = entity.lab_inputs,
        researchingSpeed = entity.get_researching_speed("normal"), energyPerTick = entity.get_max_energy_usage("normal")}
    end
  end
  for _, ingredient in ipairs(technology.research_unit_ingredients) do
    result.consumed[ingredient.name] = c.force.get_item_production_statistics(c.surface).get_output_count(ingredient.name)
  end
  for id, entity in pairs(s.known) do
    if entity.valid and entity.surface == c.surface and entity.force == c.force and entity.type == "lab" then
      U.check(#result.labs < 256, "research_observation_budget", "Known labs exceed the atomic observation budget")
      local inventory = entity.get_inventory(defines.inventory.lab_input)
      local lab = {id = id, name = entity.name, position = U.copy(entity.position),
        networkId = entity.electric_network_id, energy = entity.energy, items = U.inventory(inventory), scienceUnits = science_units(inventory),
        productivityBonus = entity.productivity_bonus, speedBonus = entity.speed_bonus}
      result.labs[#result.labs + 1] = lab
    end
  end
  table.sort(result.labs, function(a, b) return a.id < b.id end)
  return result
end

return M
