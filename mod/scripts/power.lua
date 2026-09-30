local U = require("scripts.util")
local Actor = require("scripts.actor")
local M = {}
local status_names = {}
for name, value in pairs(defines.entity_status) do status_names[value] = name end
local maximum_entities, maximum_networks, maximum_traversal = 20000, 64, 4096
-- Steam passes through pipes and chained generators; boilers, pumps and tanks end a traversal.
local steam_path = {generator = true, pipe = true, ["pipe-to-ground"] = true}

local function fed_generators(boiler)
  local found, seen, queue, head = {}, {[U.entity_id(boiler)] = true}, {boiler}, 1
  while queue[head] do
    local entity = queue[head]
    head = head + 1
    for index = 1, #entity.fluidbox do
      for _, target in ipairs(entity.fluidbox.get_connections(index)) do
        local owner = target.owner
        if owner and owner.valid and owner.force == boiler.force and steam_path[owner.type] then
          local id = U.entity_id(owner)
          if not seen[id] then
            seen[id] = true
            U.check(#queue < maximum_traversal, "power_observation_budget", "Steam traversal exceeds the observation budget")
            queue[#queue + 1] = owner
            if owner.type == "generator" then found[#found + 1] = id end
          end
        end
      end
    end
  end
  table.sort(found)
  return found
end

local function sorted(values, key)
  table.sort(values, function(a, b) return a[key] < b[key] end)
  return values
end

-- Read-only: generator capacity, consumer demand and native statistics per known own electric network.
function M.observe(args)
  local c, s = Actor.get(), Actor.state()
  U.check(c ~= nil, "actor_dead", "Power observation requires the living actor")
  local wanted = args.networkId ~= nil and U.number(args.networkId, "networkId", 0, 4294967295, nil, true) or nil
  -- Like observe and factory_snapshot, own entities near the actor join the known registry.
  for _, entity in ipairs(c.surface.find_entities_filtered{position = c.position, radius = 32, force = c.force}) do
    if entity ~= c then s.known[U.entity_id(entity)] = entity end
  end
  local networks, order, boilers, count = {}, {}, {}, 0
  local function network(id)
    if not networks[id] then
      U.check(#order < maximum_networks, "power_observation_budget", "Known electric networks exceed the observation budget")
      networks[id] = {networkId = id, poles = 0, sources = {}, consumers = {}, byName = {}}
      order[#order + 1] = id
    end
    return networks[id]
  end
  local function consume(net, name, energy, drain)
    local entry = net.byName[name]
    if not entry then
      entry = {name = name, count = 0, energyPerTick = 0, drainPerTick = 0}
      net.byName[name], net.consumers[#net.consumers + 1] = entry, entry
    end
    entry.count, entry.energyPerTick, entry.drainPerTick = entry.count + 1, entry.energyPerTick + energy, entry.drainPerTick + drain
  end
  for id, entity in pairs(s.known) do
    if entity.valid and entity.force == c.force and entity.surface == c.surface then
      count = count + 1
      U.check(count <= maximum_entities, "power_observation_budget", "Known entities exceed the observation budget")
      local prototype, net = entity.prototype, entity.electric_network_id
      if entity.type == "boiler" then
        boilers[#boilers + 1] = entity
      elseif net and (wanted == nil or net == wanted) then
        local n = network(net)
        local source = {id = id, name = entity.name, type = entity.type, position = U.copy(entity.position),
          status = entity.status and status_names[entity.status]}
        if entity.type == "electric-pole" then
          n.poles = n.poles + 1
          if not n.pole or id < n.poleId then n.pole, n.poleId = entity, id end
        elseif entity.type == "generator" or entity.type == "burner-generator" then
          source.maxPowerOutput = prototype.get_max_power_output(entity.quality) or 0
          if entity.type == "generator" then source.generatedLastTick = entity.energy_generated_last_tick end
          n.sources[#n.sources + 1] = source
        elseif entity.type == "solar-panel" then
          source.maxPowerOutput = prototype.get_max_energy_production(entity.quality)
          n.sources[#n.sources + 1] = source
        elseif entity.type == "electric-energy-interface" then
          -- Interfaces are configured at runtime; their prototype maximum is not a real load.
          if entity.power_production > 0 then source.maxPowerOutput = entity.power_production; n.sources[#n.sources + 1] = source end
          if entity.power_usage > 0 then consume(n, entity.name, entity.power_usage, 0) end
        elseif prototype.electric_energy_source_prototype and entity.type ~= "accumulator" then
          consume(n, entity.name, prototype.get_max_energy_usage(entity.quality), prototype.electric_energy_source_prototype.drain)
        end
      end
    end
  end
  local result = {scope = Actor.scope(), collectedTick = game.tick, surfaceIndex = c.surface.index, knownEntityCount = count,
    atomic = true, networks = {}, boilers = {}}
  table.sort(order)
  for _, id in ipairs(order) do
    local n = networks[id]
    local value = {networkId = id, poles = n.poles, sources = sorted(n.sources, "id"), consumers = sorted(n.consumers, "name")}
    if n.pole then
      local statistics, input, output = n.pole.electric_network_statistics, 0, 0
      local precision = defines.flow_precision_index.five_seconds
      -- Electric statistics are per tick: input is consumption, output is production.
      for name in pairs(statistics.input_counts) do
        input = input + statistics.get_flow_count{name = name, category = "input", precision_index = precision}
      end
      for name in pairs(statistics.output_counts) do
        output = output + statistics.get_flow_count{name = name, category = "output", precision_index = precision}
      end
      value.statistics = {precision = "five_seconds", consumption = input, production = output}
    end
    result.networks[#result.networks + 1] = value
  end
  for _, boiler in ipairs(boilers) do
    local generators = fed_generators(boiler)
    result.boilers[#result.boilers + 1] = {id = U.entity_id(boiler), name = boiler.name, position = U.copy(boiler.position),
      direction = boiler.direction, energyPerTick = boiler.prototype.get_max_energy_usage(boiler.quality),
      effectivity = boiler.prototype.burner_prototype and boiler.prototype.burner_prototype.effectivity or 1,
      fuel = U.inventory(boiler.get_fuel_inventory()), generatorIds = generators,
      status = boiler.status and status_names[boiler.status]}
  end
  sorted(result.boilers, "id")
  return result
end

return M
