local U = require("scripts.util")
local Actor = require("scripts.actor")
local M = {}
local maximum_names, maximum_radius, maximum_limit = 8, 1024, 256

-- Distance from a point to the square of a chunk; zero inside it.
local function chunk_distance(center, x, y)
  local dx = math.max(x * 32 - center.x, 0, center.x - (x + 1) * 32)
  local dy = math.max(y * 32 - center.y, 0, center.y - (y + 1) * 32)
  return math.sqrt(dx * dx + dy * dy)
end

-- Read-only map reading of resource patches, like a player looking at the map. Only chunks the actor's force has charted,
-- or whose normal charting it has requested (character square, radar sectors), are read: without a connected player the
-- engine leaves those requests pending instead of drawing them. Only resource entities of the requested names are read;
-- enemies and every other entity stay unexported. One aggregate per resource name and chunk, nearest chunks first.
function M.observe(args)
  local c = Actor.get()
  U.check(c ~= nil, "actor_dead", "Wait for the character to respawn")
  U.check(type(args.names) == "table", "invalid_arguments", "names must be an array")
  local total = 0
  for _ in pairs(args.names) do total = total + 1 end
  U.check(total >= 1 and total <= maximum_names and #args.names == total, "invalid_arguments",
    "Supply 1 to 8 resource names as an array")
  local names, wanted = {}, {}
  for _, name in ipairs(args.names) do
    U.string(name, "name")
    local prototype = prototypes.entity[name]
    U.check(prototype ~= nil and prototype.type == "resource", "not_resource", "Requested name is not a native resource")
    U.check(not wanted[name], "invalid_arguments", "Resource names must be distinct")
    wanted[name] = true
    names[#names + 1] = name
  end
  local center = args.center ~= nil and U.position(args.center, "center") or U.copy(c.position)
  local radius = U.number(args.radius, "radius", 32, maximum_radius, 512, true)
  local limit = U.number(args.limit, "limit", maximum_names, maximum_limit, 64, true)
  local surface, force = c.surface, c.force
  local chunks = {}
  for x = math.floor((center.x - radius) / 32), math.floor((center.x + radius) / 32) do
    for y = math.floor((center.y - radius) / 32), math.floor((center.y + radius) / 32) do
      local distance = chunk_distance(center, x, y)
      if distance <= radius then chunks[#chunks + 1] = {x = x, y = y, distance = distance} end
    end
  end
  table.sort(chunks, function(a, b)
    if a.distance ~= b.distance then return a.distance < b.distance end
    if a.y ~= b.y then return a.y < b.y end
    return a.x < b.x
  end)
  local deposits, charted, requested, entities = {}, 0, 0, 0
  local truncated, complete_radius = false, radius
  for _, chunk in ipairs(chunks) do
    local position = {x = chunk.x, y = chunk.y}
    local is_charted = force.is_chunk_charted(surface, position)
    if (is_charted or force.is_chunk_requested_for_charting(surface, position)) and surface.is_chunk_generated(position) then
      local found = surface.find_entities_filtered{area = {{chunk.x * 32, chunk.y * 32}, {chunk.x * 32 + 32, chunk.y * 32 + 32}},
        type = "resource", name = names}
      local aggregates, order = {}, {}
      for _, entity in ipairs(found) do
        local p = entity.position
        -- An area search also returns a border deposit of the neighbouring chunk; each entity counts in its own chunk.
        if math.floor(p.x / 32) == chunk.x and math.floor(p.y / 32) == chunk.y then
          local a = aggregates[entity.name]
          if not a then
            a = {count = 0, amount = 0}
            aggregates[entity.name], order[#order + 1] = a, entity.name
          end
          a.count, a.amount = a.count + 1, a.amount + entity.amount
          local d = (p.x - center.x) ^ 2 + (p.y - center.y) ^ 2
          if not a.position or d < a.distance or (d == a.distance and (p.y < a.position.y
              or (p.y == a.position.y and p.x < a.position.x))) then
            a.position, a.distance, a.id = U.copy(p), d, U.entity_id(entity)
          end
        end
      end
      -- Whole chunks only: every point nearer than complete_radius lies in a chunk read in full.
      if #deposits + #order > limit then
        truncated, complete_radius = true, chunk.distance
        break
      end
      table.sort(order)
      for _, name in ipairs(order) do
        local a = aggregates[name]
        deposits[#deposits + 1] = {name = name, chunk = {x = chunk.x, y = chunk.y}, charted = is_charted,
          sample = {id = a.id, position = a.position}, count = a.count, amount = a.amount}
        entities = entities + a.count
      end
      if is_charted then charted = charted + 1 else requested = requested + 1 end
    end
  end
  return {scope = Actor.scope(), collectedTick = game.tick, surfaceIndex = surface.index, center = center, radius = radius,
    names = names, limit = limit, deposits = deposits,
    coverage = {visibility = "force-charted-or-requested-chunks", consideredChunks = #chunks, chartedChunks = charted,
      requestedChunks = requested, entities = entities, truncated = truncated, completeRadius = complete_radius}}
end

return M
