local M = {}

function M.records()
  local s = storage.agent
  s.corpses = s.corpses or {}
  for id, record in pairs(s.corpses) do
    if not record.entity.valid then s.corpses[id] = nil end
  end
  return s.corpses
end

function M.register(event)
  local s = storage.agent
  local death = s and s.lastDeath
  if not death or death.unitNumber ~= event.unit_number or death.tick ~= event.tick
    or death.surfaceIndex ~= event.surface_index then return end
  local records = M.records()
  for index, entity in ipairs(event.corpses) do
    if entity.valid and entity.type == "character-corpse" then
      -- Corpses are neutral and have no unit_number. Position alone cannot
      -- distinguish repeated deaths in the same place or another player's body.
      local id = "corpse:" .. death.unitNumber .. ":" .. death.tick .. ":" .. index
      records[id] = {entity = entity, deathTick = death.tick, incarnation = death.incarnation,
        actorUnitNumber = death.unitNumber}
    end
  end
end

function M.find(id)
  local record = M.records()[id]
  return record and record.entity
end

function M.id(entity)
  if entity.type ~= "character-corpse" then return end
  for id, record in pairs(M.records()) do
    if record.entity == entity then return id end
  end
end

return M
