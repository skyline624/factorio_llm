local M = {}

function M.fail(code, message)
  error({code = code, message = message}, 0)
end

function M.check(condition, code, message)
  if not condition then M.fail(code, message) end
end

function M.number(value, name, minimum, maximum, default, integer)
  if value == nil then value = default end
  M.check(type(value) == "number" and value == value and value >= minimum and value <= maximum
    and (not integer or value == math.floor(value)), "invalid_arguments", name .. " is out of bounds")
  return value
end

function M.string(value, name, default)
  if value == nil then value = default end
  M.check(type(value) == "string" and #value > 0 and #value <= 128,
    "invalid_arguments", name .. " must be a non-empty string of at most 128 bytes")
  return value
end

function M.position(value, name)
  M.check(type(value) == "table", "invalid_arguments", (name or "position") .. " is required")
  return {x = M.number(value.x, "x", -1000000, 1000000),
    y = M.number(value.y, "y", -1000000, 1000000)}
end

function M.distance(a, b)
  return math.sqrt((a.x - b.x) ^ 2 + (a.y - b.y) ^ 2)
end

function M.copy(value)
  if type(value) ~= "table" then return value end
  local result = {}
  for key, item in pairs(value) do result[key] = M.copy(item) end
  return result
end

-- Key order must not change the identity of a serialized request.
function M.canonical(value, depth)
  depth = depth or 0
  M.check(depth <= 16, "invalid_arguments", "Maximum JSON nesting exceeded")
  if type(value) == "number" then return "number:" .. string.format("%.17g", value) end
  if type(value) ~= "table" then return type(value) .. ":" .. tostring(value) end
  local keys, parts = {}, {}
  for key in pairs(value) do keys[#keys + 1] = key end
  table.sort(keys, function(a, b) return tostring(a) < tostring(b) end)
  for _, key in ipairs(keys) do
    local k, v = M.canonical(key, depth + 1), M.canonical(value[key], depth + 1)
    parts[#parts + 1] = #k .. ":" .. k .. #v .. ":" .. v
  end
  return "{" .. table.concat(parts) .. "}"
end

function M.inventory(inventory)
  local counts = {}
  if inventory and inventory.valid then
    for _, stack in ipairs(inventory.get_contents()) do
      local quality = stack.quality
      if type(quality) == "table" then quality = quality.name end
      local key = stack.name
      if quality and quality ~= "normal" then key = key .. "@" .. quality end
      counts[key] = (counts[key] or 0) + stack.count
    end
  end
  return counts
end

function M.delta(before, after)
  local delta = {}
  for name, count in pairs(after) do
    if count ~= (before[name] or 0) then delta[name] = count - (before[name] or 0) end
  end
  for name, count in pairs(before) do
    if after[name] == nil then delta[name] = -count end
  end
  return delta
end

function M.ammo(inventory)
  local rounds = 0
  if not inventory then return rounds end
  for index = 1, #inventory do
    local stack = inventory[index]
    if stack.valid_for_read and stack.prototype.type == "ammo" then
      rounds = rounds + (stack.count - 1) * stack.prototype.magazine_size + stack.ammo
    end
  end
  return rounds
end

function M.error(value)
  if type(value) == "table" and value.code then return value end
  return {code = "engine_error", message = tostring(value)}
end

function M.entity_id(entity)
  if entity.unit_number then return tostring(entity.unit_number) end
  return entity.surface.index .. ":" .. entity.name .. ":" .. entity.position.x .. ":" .. entity.position.y
end

return M
